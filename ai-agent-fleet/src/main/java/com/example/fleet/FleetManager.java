package com.example.fleet;

import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * The fleet: a pool of identical agents sharing one task queue, plus a supervisor that
 * (1) autoscales the pool with queue depth, (2) replaces agents whose heartbeat goes stale and
 * recovers their in-flight task, and (3) retries failed tasks, dead-lettering after MAX_ATTEMPTS.
 */
public class FleetManager {

    static final int MAX_ATTEMPTS = 4;

    private final int minAgents;
    private final int maxAgents;
    private final Model model;
    private final long heartbeatTimeoutMs;
    private final LinkedBlockingQueue<Task> queue = new LinkedBlockingQueue<>();
    private final Map<String, Agent> agents = new ConcurrentHashMap<>();
    private final List<String> results = new CopyOnWriteArrayList<>();
    private final List<Task> deadLetters = new CopyOnWriteArrayList<>();
    private final Set<Integer> resolvedIds = ConcurrentHashMap.newKeySet();
    private final AtomicInteger agentSeq = new AtomicInteger();
    private final AtomicInteger restarts = new AtomicInteger();
    private final AtomicInteger peak = new AtomicInteger();
    private final CountDownLatch resolved;
    private final ScheduledExecutorService supervisor = Executors.newSingleThreadScheduledExecutor();

    public FleetManager(int minAgents, int maxAgents, Model model, long heartbeatTimeoutMs, int totalTasks) {
        this.minAgents = minAgents;
        this.maxAgents = maxAgents;
        this.model = model;
        this.heartbeatTimeoutMs = heartbeatTimeoutMs;
        this.resolved = new CountDownLatch(totalTasks);
    }

    public void start() {
        for (int i = 0; i < minAgents; i++) {
            spawn("start");
        }
        supervisor.scheduleWithFixedDelay(this::supervise, 250, 250, TimeUnit.MILLISECONDS);
    }

    public void submit(Task task) {
        queue.add(task);
    }

    public boolean awaitCompletion(long timeout, TimeUnit unit) throws InterruptedException {
        return resolved.await(timeout, unit);
    }

    public void shutdown() {
        supervisor.shutdownNow();
        agents.values().forEach(Agent::stop);
    }

    // Called by agents

    Task nextTask() throws InterruptedException {
        return queue.poll(100, TimeUnit.MILLISECONDS);
    }

    void reportSuccess(String agent, Task task, String answer) {
        if (!resolvedIds.add(task.id())) {
            return; // already resolved, e.g. a replaced agent finishing a task that was re-queued
        }
        results.add(answer);
        String preview = answer.replaceAll("\\s+", " ");
        log(agent + " completed " + task + ": " + (preview.length() > 70 ? preview.substring(0, 70) + "..." : preview));
        resolved.countDown();
    }

    void reportFailure(String agent, Task task, String reason) {
        log(agent + " failed " + task + " (" + reason + ")");
        retryOrDeadLetter(task);
    }

    // Supervision

    private void supervise() {
        try {
            long now = System.currentTimeMillis();
            for (Agent agent : List.copyOf(agents.values())) {
                if (now - agent.lastHeartbeat() > heartbeatTimeoutMs) {
                    replace(agent);
                }
            }
            autoscale();
            log(String.format("status: agents=%d busy=%d queued=%d done=%d dead-lettered=%d",
                    agents.size(), agents.values().stream().filter(Agent::isBusy).count(),
                    queue.size(), results.size(), deadLetters.size()));
        } catch (RuntimeException e) {
            log("supervisor error: " + e);
        }
    }

    private void replace(Agent dead) {
        agents.remove(dead.name());
        dead.stop();
        log("!! " + dead.name() + " missed heartbeat, replacing");
        Task lost = dead.currentTask();
        if (lost != null) {
            retryOrDeadLetter(lost);
        }
        restarts.incrementAndGet();
        spawn("replacement");
    }

    private void autoscale() {
        // Add one agent per tick while there are more than 2 queued tasks per agent.
        if (queue.size() > agents.size() * 2 && agents.size() < maxAgents) {
            spawn("scale-up");
        }
    }

    private void retryOrDeadLetter(Task task) {
        if (task.attempts() + 1 >= MAX_ATTEMPTS) {
            if (!resolvedIds.add(task.id())) {
                return;
            }
            deadLetters.add(task);
            log("xx " + task + " dead-lettered");
            resolved.countDown();
        } else {
            // Exponential backoff so retries don't hammer a rate-limited model.
            long delayMs = 1000L << task.attempts();
            supervisor.schedule(() -> queue.add(task.retry()), delayMs, TimeUnit.MILLISECONDS);
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
    }
}
