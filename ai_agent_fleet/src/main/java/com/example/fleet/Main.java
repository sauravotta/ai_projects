package com.example.fleet;

import java.util.concurrent.TimeUnit;

/** Demo: a fleet of 2 agents (scaling to 8) works through 40 tasks while some agents crash. */
public class Main {

    public static void main(String[] args) throws InterruptedException {
        int tasks = args.length > 0 ? Integer.parseInt(args[0]) : 40;
        FleetManager fleet = new FleetManager(2, 8, 0.08, tasks);
        fleet.start();
        for (int i = 1; i <= tasks; i++) {
            fleet.submit(new Task(i, "summarize document #" + i, 0));
        }
        boolean done = fleet.awaitCompletion(2, TimeUnit.MINUTES);
        fleet.shutdown();
        fleet.printSummary();
        if (!done) {
            System.out.println("Timed out before all tasks resolved.");
            System.exit(1);
        }
    }
}
