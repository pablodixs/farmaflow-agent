package com.scriptles.farmaflowagent.dto;

public record AgentStatusResponse(
        OperatingSystemDto operatingSystem,
        HardwareDto hardware,
        InternetDto internet
) {
    public record OperatingSystemDto(
            String name,
            String version,
            String architecture
    ) {
    }

    public record HardwareDto(
            String computerName,
            int availableProcessors,
            long maxMemoryBytes,
            long totalMemoryBytes,
            long freeMemoryBytes
    ) {
    }

    public record InternetDto(
            boolean connected,
            String checkedUrl,
            long latencyMs,
            String error
    ) {
    }
}
