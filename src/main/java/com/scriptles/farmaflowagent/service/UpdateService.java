package com.scriptles.farmaflowagent.service;

import com.scriptles.farmaflowagent.dto.UpdateCheckResponse;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;
import tools.jackson.databind.JsonNode;
import tools.jackson.databind.ObjectMapper;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;

@Service
public class UpdateService {

    private final String currentVersion;
    private final String manifestUrl;
    private final ObjectMapper objectMapper;
    private final HttpClient httpClient;

    public UpdateService(
            @Value("${agent.version:0.0.1-SNAPSHOT}") String currentVersion,
            @Value("${agent.update.manifest-url:}") String manifestUrl,
            ObjectMapper objectMapper
    ) {
        this.currentVersion = currentVersion;
        this.manifestUrl = manifestUrl;
        this.objectMapper = objectMapper;
        this.httpClient = HttpClient.newBuilder()
                .connectTimeout(Duration.ofSeconds(5))
                .build();
    }

    public UpdateCheckResponse checkForUpdate() {
        if (manifestUrl == null || manifestUrl.isBlank()) {
            return new UpdateCheckResponse(
                    currentVersion,
                    false,
                    false,
                    null,
                    null,
                    null,
                    "agent.update.manifest-url nao configurado"
            );
        }

        try {
            HttpRequest request = HttpRequest.newBuilder(URI.create(manifestUrl))
                    .timeout(Duration.ofSeconds(10))
                    .GET()
                    .build();

            HttpResponse<String> response = httpClient.send(request, HttpResponse.BodyHandlers.ofString());

            if (response.statusCode() < 200 || response.statusCode() >= 300) {
                return failed("HTTP " + response.statusCode());
            }

            JsonNode manifest = objectMapper.readTree(response.body());
            String latestVersion = text(manifest, "version");

            return new UpdateCheckResponse(
                    currentVersion,
                    true,
                    isNewer(latestVersion, currentVersion),
                    latestVersion,
                    text(manifest, "downloadUrl"),
                    text(manifest, "releaseNotes"),
                    null
            );
        } catch (Exception exception) {
            return failed(exception.getMessage());
        }
    }

    private UpdateCheckResponse failed(String error) {
        return new UpdateCheckResponse(
                currentVersion,
                true,
                false,
                null,
                null,
                null,
                error
        );
    }

    private boolean isNewer(String latestVersion, String currentVersion) {
        if (latestVersion == null || latestVersion.isBlank()) {
            return false;
        }

        int[] latest = versionParts(latestVersion);
        int[] current = versionParts(currentVersion);

        for (int i = 0; i < Math.max(latest.length, current.length); i++) {
            int latestPart = i < latest.length ? latest[i] : 0;
            int currentPart = i < current.length ? current[i] : 0;

            if (latestPart != currentPart) {
                return latestPart > currentPart;
            }
        }

        return false;
    }

    private String text(JsonNode node, String fieldName) {
        JsonNode value = node.get(fieldName);
        return value == null || value.isNull() ? null : value.asText();
    }

    private int[] versionParts(String version) {
        String normalized = version == null
                ? ""
                : version.replaceFirst("^v", "").replace("-SNAPSHOT", "");

        String[] parts = normalized.split("\\.");
        int[] result = new int[parts.length];

        for (int i = 0; i < parts.length; i++) {
            result[i] = numberPrefix(parts[i]);
        }

        return result;
    }

    private int numberPrefix(String value) {
        String digits = value.replaceFirst("^(\\d+).*$", "$1");

        if (digits.isBlank() || !digits.chars().allMatch(Character::isDigit)) {
            return 0;
        }

        return Integer.parseInt(digits);
    }
}
