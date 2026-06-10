package com.scriptles.farmaflowagent.dto;

public record PdfPrintStartResponse(
        String jobId,
        String status,
        int progress,
        String message,
        String statusUrl,
        String error
) {
}
