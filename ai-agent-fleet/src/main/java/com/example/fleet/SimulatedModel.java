package com.example.fleet;

import java.io.IOException;
import java.util.concurrent.ThreadLocalRandom;

/** Offline stand-in for an LLM: random latency, soft failures, and hard crashes. */
public class SimulatedModel implements Model {

    private final double crashRate;

    public SimulatedModel(double crashRate) {
        this.crashRate = crashRate;
    }

    @Override
    public String complete(String prompt) throws Exception {
        ThreadLocalRandom rnd = ThreadLocalRandom.current();
        Thread.sleep(rnd.nextInt(150, 400));
        if (rnd.nextDouble() < crashRate) {
            throw new IllegalStateException("simulated hard crash");
        }
        if (rnd.nextDouble() < 0.1) {
            throw new IOException("simulated model timeout");
        }
        return "answer for \"" + prompt + "\"";
    }
}
