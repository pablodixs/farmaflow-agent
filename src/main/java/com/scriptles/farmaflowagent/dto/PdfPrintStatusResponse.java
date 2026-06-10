package com.scriptles.farmaflowagent.dto;

public record PdfPrintStatusResponse(
        String jobId,
        String status,
        int progress,
        String message,
        String printerName,
        String fileName,
        String type,
        String paperWidth,
        String error
) {
}
