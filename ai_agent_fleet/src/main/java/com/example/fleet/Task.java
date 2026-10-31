package com.example.fleet;

/** A unit of work for the fleet. Immutable; a retry creates a new instance with attempts + 1. */
public record Task(int id, String prompt, int attempts) {

    public Task retry() {
        return new Task(id, prompt, attempts + 1);
    }

    @Override
    public String toString() {
        return "task-" + id + (attempts > 0 ? " (attempt " + (attempts + 1) + ")" : "");
    }
}
