package com.example.fleet;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.TimeUnit;

/**
 * Demo: a fleet works through N tasks. With OPENROUTER_API_KEY set (env var or ./.env) it calls a
 * real model; otherwise it uses the offline simulated model with random crashes.
 */
public class Main {

    private static final String DEFAULT_MODEL = "nvidia/nemotron-3-ultra-550b-a55b:free";

    private static final List<String> FALLBACK_MODELS = List.of(
            "nvidia/nemotron-3.5-lightning:free",
            "google/gemma-4-31b-it:free",
            "google/gemma-4-26b-a4b-it:free");

    public static void main(String[] args) throws InterruptedException {
        String apiKey = setting("OPENROUTER_API_KEY");
        boolean live = apiKey != null && !apiKey.isBlank();
        int tasks = args.length > 0 ? Integer.parseInt(args[0]) : (live ? 6 : 40);

        FleetManager fleet;
        if (live) {
            String model = setting("OPENROUTER_MODEL");
            List<String> models = new ArrayList<>();
            models.add(model == null || model.isBlank() ? DEFAULT_MODEL : model);
            models.addAll(FALLBACK_MODELS);
            List<String> chain = models.stream().distinct().toList();
            Model llm = new OpenRouterModel(apiKey, chain);
            // Small fleet (free models are rate-limited); heartbeat timeout covers the worst-case fallback chain.
            long timeout = OpenRouterModel.REQUEST_TIMEOUT.toMillis() * chain.size() + 15_000;
            fleet = new FleetManager(2, 3, llm, timeout, tasks, databasePath());
            System.out.println("Mode: live OpenRouter model");
        } else {
            fleet = new FleetManager(2, 8, new SimulatedModel(0.08), 1500, tasks, databasePath());
            System.out.println("Mode: simulated model (set OPENROUTER_API_KEY for a real LLM)");
        }
        fleet.start();
        for (int i = 1; i <= tasks; i++) {
            fleet.submitNew("In one sentence, explain topic #" + i + " of an intro AI course.");
        }

        boolean done = fleet.awaitCompletion(live ? 5 : 2, TimeUnit.MINUTES);
        fleet.shutdown();
        fleet.printSummary();
        if (!done) {
            System.out.println("Timed out before all tasks resolved.");
            System.exit(1);
        }
    }

    private static Path databasePath() {
        String path = setting("FLEET_DB_PATH");
        return path == null || path.isBlank() ? Path.of("fleet.db") : Path.of(path);
    }

    /** Reads a setting from the environment, falling back to KEY=value lines in ./.env. */
    private static String setting(String key) {
        String value = System.getenv(key);
        if (value != null && !value.isBlank()) {
            return value;
        }
        Path file = Path.of(".env");
        if (!Files.isRegularFile(file)) {
            return null;
        }
        try {
            for (String line : Files.readAllLines(file)) {
                line = line.strip();
                if (line.startsWith(key + "=")) {
                    return line.substring(key.length() + 1).strip().replaceAll("^[\"']|[\"']$", "");
                }
            }
        } catch (IOException e) {
            // Treat an unreadable .env as absent.
        }
        return null;
    }
}
