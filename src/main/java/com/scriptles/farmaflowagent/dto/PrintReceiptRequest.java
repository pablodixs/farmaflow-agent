package com.scriptles.farmaflowagent.dto;

import java.math.BigDecimal;
import java.util.List;

public record PrintReceiptRequest(
        String type,
        String printerName,
        String paperWidth,
        StoreDto store,
        CustomerDto customer,
        List<ItemDto> items,
        BigDecimal total,
        String paymentMethod
) {
    public record StoreDto(
            String name,
            String cnpj,
            String address
    ) {
    }

    public record CustomerDto(
            String name,
            String phone,
            String address
    ) {
    }

    public record ItemDto(
            String name,
            Integer quantity,
            BigDecimal unitPrice,
            BigDecimal total
    ) {
    }
}