package com.scriptles.farmaflowagent.dto;

public record PrintTestRequest(
        String printerName,
        String paperWidth
) {
}
