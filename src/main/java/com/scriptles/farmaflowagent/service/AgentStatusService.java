package com.scriptles.farmaflowagent.service;

import com.scriptles.farmaflowagent.dto.AgentStatusResponse;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;

import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.URI;
import java.time.Duration;

@Service
public class AgentStatusService {

    private final String internetCheckUrl;
    private final int internetTimeoutMs;

    public AgentStatusService(
            @Value("${agent.internet-check.url:https://www.google.com/generate_204}") String internetCheckUrl,
            @Value("${agent.internet-check.timeout-ms:3000}") int internetTimeoutMs
    ) {
        this.internetCheckUrl = internetCheckUrl;
        this.internetTimeoutMs = internetTimeoutMs;
    }

    public AgentStatusResponse status() {
        Runtime runtime = Runtime.getRuntime();

        return new AgentStatusResponse(
                new AgentStatusResponse.OperatingSystemDto(
                        System.getProperty("os.name"),
                        System.getProperty("os.version"),
                        System.getProperty("os.arch")
                ),
                new AgentStatusResponse.HardwareDto(
                        computerName(),
                        runtime.availableProcessors(),
                        runtime.maxMemory(),
                        runtime.totalMemory(),
                        runtime.freeMemory()
                ),
                checkInternet()
        );
    }

    private String computerName() {
        String computerName = System.getenv("COMPUTERNAME");

        if (computerName == null || computerName.isBlank()) {
            computerName = System.getenv("HOSTNAME");
        }

        if (computerName == null || computerName.isBlank()) {
            try {
                computerName = InetAddress.getLocalHost().getHostName();
            } catch (Exception exception) {
                computerName = "desconhecido";
            }
        }

        return computerName;
    }

    private AgentStatusResponse.InternetDto checkInternet() {
        long startedAt = System.nanoTime();

        try {
            HttpURLConnection connection = (HttpURLConnection) URI.create(internetCheckUrl)
                    .toURL()
                    .openConnection();
            connection.setRequestMethod("HEAD");
            connection.setConnectTimeout(internetTimeoutMs);
            connection.setReadTimeout(internetTimeoutMs);
            connection.connect();

            int statusCode = connection.getResponseCode();
            boolean connected = statusCode >= 200 && statusCode < 400;

            return new AgentStatusResponse.InternetDto(
                    connected,
                    internetCheckUrl,
                    elapsedMs(startedAt),
                    connected ? null : "HTTP " + statusCode
            );
        } catch (Exception exception) {
            return new AgentStatusResponse.InternetDto(
                    false,
                    internetCheckUrl,
                    elapsedMs(startedAt),
                    exception.getMessage()
            );
        }
    }

    private long elapsedMs(long startedAt) {
        return Duration.ofNanos(System.nanoTime() - startedAt).toMillis();
    }
}
