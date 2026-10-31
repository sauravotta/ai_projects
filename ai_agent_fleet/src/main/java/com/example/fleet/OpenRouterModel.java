package com.example.fleet;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.List;
import java.util.Map;

/** Calls an OpenAI-compatible chat endpoint (OpenRouter). Non-200 responses become retryable failures. */
public class OpenRouterModel implements Model {

    static final Duration REQUEST_TIMEOUT = Duration.ofSeconds(30);
    private static final URI ENDPOINT = URI.create("https://openrouter.ai/api/v1/chat/completions");

    private final HttpClient http = HttpClient.newHttpClient();
    private final ObjectMapper json = new ObjectMapper();
    private final String apiKey;
    private final List<String> models;

    public OpenRouterModel(String apiKey, List<String> models) {
        this.apiKey = apiKey;
        this.models = models;
    }

    @Override
    public String complete(String prompt) throws Exception {
        IOException last = null;
        // Fall back through the model list when one is rate-limited or unavailable.
        for (String model : models) {
            try {
                return call(model, prompt);
            } catch (ModelUnavailableException e) {
                last = e;
            }
        }
        throw last != null ? last : new IOException("no models configured");
    }

    private String call(String model, String prompt) throws Exception {
        String body = json.writeValueAsString(Map.of(
                "model", model,
                "messages", List.of(Map.of("role", "user", "content", prompt))));
        HttpRequest request = HttpRequest.newBuilder(ENDPOINT)
                .timeout(REQUEST_TIMEOUT)
                .header("Authorization", "Bearer " + apiKey)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build();
        HttpResponse<String> response = http.send(request, HttpResponse.BodyHandlers.ofString());
        int status = response.statusCode();
        if (status == 429 || status == 403 || status == 404) {
            throw new ModelUnavailableException("HTTP " + status + " from " + model);
        }
        if (status != 200) {
            throw new IOException("HTTP " + status + " from " + model);
        }
        JsonNode content = json.readTree(response.body()).at("/choices/0/message/content");
        if (content.isMissingNode() || content.asText().isBlank()) {
            throw new IOException("empty model response from " + model);
        }
        return content.asText().strip();
    }

    private static class ModelUnavailableException extends IOException {
        ModelUnavailableException(String message) {
            super(message);
        }
    }
}
