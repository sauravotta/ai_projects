package com.example.fleet;

/**
 * One identical worker in the fleet. It pulls tasks from the shared queue, "thinks" (simulated
 * LLM call), and reports back. It beats a heartbeat so the manager can detect a dead agent.
 */
public class Agent implements Runnable {

    private final String name;
    private final FleetManager fleet;
    private final Model model;

    private volatile long lastHeartbeat = System.currentTimeMillis();
    private volatile Task current;
    private volatile boolean stopped;

    public Agent(String name, FleetManager fleet, Model model) {
        this.name = name;
        this.fleet = fleet;
        this.model = model;
    }

    @Override
    public void run() {
        try {
            while (!stopped) {
                beat();
                Task task = fleet.nextTask(name);
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

    private void work(Task task) {
        try {
            fleet.reportSuccess(name, task, model.complete(task.prompt()));
        } catch (IllegalStateException crash) {
            throw crash;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (Exception softFailure) {
            // e.g. timeout or rate limit: the agent survives and the task is retried.
            fleet.reportFailure(name, task, softFailure.getMessage());
        }
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
