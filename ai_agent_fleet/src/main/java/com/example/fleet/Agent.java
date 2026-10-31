package com.example.fleet;

import java.util.concurrent.ThreadLocalRandom;

/**
 * One identical worker in the fleet. It pulls tasks from the shared queue, "thinks" (simulated
 * LLM call), and reports back. It beats a heartbeat so the manager can detect a dead agent.
 */
public class Agent implements Runnable {

    private final String name;
    private final FleetManager fleet;
    private final double crashRate;

    private volatile long lastHeartbeat = System.currentTimeMillis();
    private volatile Task current;
    private volatile boolean stopped;

    public Agent(String name, FleetManager fleet, double crashRate) {
        this.name = name;
        this.fleet = fleet;
        this.crashRate = crashRate;
    }

    @Override
    public void run() {
        try {
            while (!stopped) {
                beat();
                Task task = fleet.nextTask();
                if (task == null) {
                    continue;
                }
                current = task;
                work(task);
                current = null;
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (IllegalStateException crash) {
            // Simulated hard crash: the thread dies silently and stops beating.
            // The manager detects the stale heartbeat and recovers the in-flight task.
        }
    }

    private void work(Task task) throws InterruptedException {
        ThreadLocalRandom rnd = ThreadLocalRandom.current();
        Thread.sleep(rnd.nextInt(150, 400));
        if (rnd.nextDouble() < crashRate) {
            throw new IllegalStateException(name + " crashed on " + task);
        }
        // Soft failure, e.g. a model timeout: the agent survives and the task is retried.
        if (rnd.nextDouble() < 0.1) {
            fleet.reportFailure(name, task);
            return;
        }
        fleet.reportSuccess(name, task, "answer for \"" + task.prompt() + "\"");
    }

    private void beat() {
        lastHeartbeat = System.currentTimeMillis();
    }

    public void stop() {
        stopped = true;
    }

    public String name() {
        return name;
    }

    public long lastHeartbeat() {
        return lastHeartbeat;
    }

    public Task currentTask() {
        return current;
    }

    public boolean isBusy() {
        return current != null;
    }
}
