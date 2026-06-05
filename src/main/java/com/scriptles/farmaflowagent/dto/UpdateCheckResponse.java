package com.scriptles.farmaflowagent.dto;

public record UpdateCheckResponse(
        String currentVersion,
        boolean configured,
        boolean updateAvailable,
        String latestVersion,
        String downloadUrl,
        String releaseNotes,
        String error
) {
}
