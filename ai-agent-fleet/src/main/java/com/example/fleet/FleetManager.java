package com.example.fleet;

import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * A pool of agents backed by a durable SQLite queue. The supervisor autoscales, replaces stale
 * workers, retries failures, and refreshes the fleet-instance lease used for restart recovery.
 */
public class FleetManager {

    static final int MAX_ATTEMPTS = 4;

    private final int minAgents;
    private final int maxAgents;
    private final Model model;
    private final long heartbeatTimeoutMs;
    private final TaskStore store;
    private final Map<String, Agent> agents = new ConcurrentHashMap<>();
    private final List<String> results = new CopyOnWriteArrayList<>();
    private final List<Task> deadLetters = new CopyOnWriteArrayList<>();
    private final Set<Integer> expectedIds = ConcurrentHashMap.newKeySet();
    private final Set<Integer> countedIds = ConcurrentHashMap.newKeySet();
    private final Set<Integer> recordedIds = ConcurrentHashMap.newKeySet();
    private final AtomicInteger agentSeq = new AtomicInteger();
    private final AtomicInteger restarts = new AtomicInteger();
    private final AtomicInteger peak = new AtomicInteger();
    private final CountDownLatch resolved;
    private final ScheduledExecutorService supervisor = Executors.newSingleThreadScheduledExecutor();

    public FleetManager(int minAgents, int maxAgents, Model model, long heartbeatTimeoutMs, int totalTasks) {
        this(minAgents, maxAgents, model, heartbeatTimeoutMs, totalTasks, Path.of("fleet.db"));
    }

    public FleetManager(
            int minAgents, int maxAgents, Model model, long heartbeatTimeoutMs, int totalTasks, Path databasePath) {
        if (minAgents < 1 || maxAgents < minAgents || totalTasks < 0) {
            throw new IllegalArgumentException("Invalid fleet size or task count");
        }
        this.minAgents = minAgents;
        this.maxAgents = maxAgents;
        this.model = model;
        this.heartbeatTimeoutMs = heartbeatTimeoutMs;
        this.store = new TaskStore(databasePath, Math.max(5_000, heartbeatTimeoutMs * 2));
        this.resolved = new CountDownLatch(totalTasks);
    }

    public void start() {
        for (int i = 0; i < minAgents; i++) {
            spawn("start");
        }
        supervisor.scheduleWithFixedDelay(this::supervise, 250, 250, TimeUnit.MILLISECONDS);
    }

    /** Creates a fresh, uniquely identified durable task for this demo invocation. */
    public Task submitNew(String prompt) {
        Task task = store.create(prompt);
        registerExpected(task.id());
        recordIfResolved(task, store.snapshot(task.id()));
        return task;
    }

    /** Submits an explicit idempotency key; an existing id must have the same prompt. */
    public void submit(Task task) {
        registerExpected(task.id());
        TaskStore.TaskSnapshot snapshot = store.submit(task);
        recordIfResolved(task, snapshot);
    }

    public boolean awaitCompletion(long timeout, TimeUnit unit) throws InterruptedException {
        return resolved.await(timeout, unit);
    }

    public void shutdown() {
        supervisor.shutdownNow();
        agents.values().forEach(Agent::stop);
    }

    // Called by agents

    Task nextTask(String worker) throws InterruptedException {
        Task task = store.claim(worker);
        if (task == null) {
            Thread.sleep(100);
        }
        return task;
    }

    void reportSuccess(String agent, Task task, String answer) {
        if (!store.complete(task, answer)) {
            return;
        }
        if (expectedIds.contains(task.id())) {
            if (recordedIds.add(task.id())) {
                results.add(answer);
            }
            countResolved(task.id());
        }
        String preview = answer.replaceAll("\\s+", " ");
        log(agent + " completed " + task + ": " + (preview.length() > 70 ? preview.substring(0, 70) + "..." : preview));
    }

    void reportFailure(String agent, Task task, String reason) {
        log(agent + " failed " + task + " (" + reason + ")");
        retryOrDeadLetter(task, reason);
    }

    // Supervision

    private void supervise() {
        try {
            store.heartbeat();
            long now = System.currentTimeMillis();
            for (Agent agent : List.copyOf(agents.values())) {
                if (now - agent.lastHeartbeat() > heartbeatTimeoutMs) {
                    replace(agent);
                }
            }
            autoscale();
            log(String.format("status: agents=%d busy=%d queued=%d done=%d dead-lettered=%d",
                    agents.size(), agents.values().stream().filter(Agent::isBusy).count(),
                    store.queuedCount(), results.size(), deadLetters.size()));
        } catch (RuntimeException e) {
            log("supervisor error: " + e);
        }
    }

    private void replace(Agent dead) {
        if (!agents.remove(dead.name(), dead)) {
            return;
        }
        dead.stop();
        log("!! " + dead.name() + " missed heartbeat, replacing");
        Task lost = dead.currentTask();
        if (lost != null) {
            retryOrDeadLetter(lost, "worker heartbeat expired");
        }
        restarts.incrementAndGet();
        spawn("replacement");
    }

    private void autoscale() {
        if (store.queuedCount() > agents.size() * 2 && agents.size() < maxAgents) {
            spawn("scale-up");
        }
    }

    private void retryOrDeadLetter(Task task, String reason) {
        TaskStore.FailureOutcome outcome = store.fail(task, reason, MAX_ATTEMPTS);
        if (outcome == TaskStore.FailureOutcome.DEAD_LETTERED) {
            if (recordedIds.add(task.id())) {
                deadLetters.add(task);
            }
            log("xx " + task + " dead-lettered");
            countResolved(task.id());
        }
    }

    private void registerExpected(int taskId) {
        if (!expectedIds.add(taskId)) {
            return;
        }
    }

    private void recordIfResolved(Task task, TaskStore.TaskSnapshot snapshot) {
        if (snapshot.status().equals("completed")) {
            if (snapshot.result() != null && recordedIds.add(task.id())) {
                results.add(snapshot.result());
            }
            countResolved(task.id());
        } else if (snapshot.status().equals("dead")) {
            if (recordedIds.add(task.id())) {
                deadLetters.add(new Task(task.id(), task.prompt(), snapshot.attempts()));
            }
            countResolved(task.id());
        }
    }

    private void countResolved(int taskId) {
        if (expectedIds.contains(taskId) && countedIds.add(taskId)) {
            resolved.countDown();
        }
    }

    private void spawn(String reason) {
        String name = "agent-" + agentSeq.incrementAndGet();
        Agent agent = new Agent(name, this, model);
        agents.put(name, agent);
        peak.accumulateAndGet(agents.size(), Math::max);
        Thread.ofPlatform().name(name).daemon().start(agent);
        log("++ " + name + " started (" + reason + ")");
    }

    private static void log(String msg) {
        System.out.printf("[%tT.%<tL] %s%n", System.currentTimeMillis(), msg);
    }

    public void printSummary() {
        System.out.println("\n=== FLEET SUMMARY ===");
        System.out.println("completed:      " + results.size());
        System.out.println("dead-lettered:  " + deadLetters.size() + " " + deadLetters);
        System.out.println("agent restarts: " + restarts.get());
        System.out.println("peak fleet size: " + peak.get());
        System.out.println("durable totals:  completed=" + store.completedCount() +
                " dead-lettered=" + store.deadLetterCount());
    }
}
