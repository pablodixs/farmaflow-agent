package com.scriptles.farmaflowagent.controller;

import com.scriptles.farmaflowagent.dto.PrintReceiptRequest;
import com.scriptles.farmaflowagent.dto.PrintTestRequest;
import com.scriptles.farmaflowagent.service.PrinterService;
import org.springframework.web.bind.annotation.CrossOrigin;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.util.Map;

@RestController
@RequestMapping("/print")
@CrossOrigin(
        origins = {
                "http://localhost:3000",
                "https://farmaflow-rho.vercel.app/"
        }
)
public class PrintController {

    private final PrinterService printerService;

    public PrintController(PrinterService printerService) {
        this.printerService = printerService;
    }

    @GetMapping("/printers")
    public Object listPrinters() {
        return printerService.listPrinters();
    }

    @PostMapping("/sale")
    public Map<String, Object> printSale(@RequestBody PrintReceiptRequest request) {
        printerService.printReceipt(request);

        return Map.of(
                "success", true,
                "message", "Cupom enviado para impressao"
        );
    }

    @PostMapping("/delivery")
    public Map<String, Object> printDelivery(@RequestBody PrintReceiptRequest request) {
        printerService.printReceipt(request);

        return Map.of(
                "success", true,
                "message", "Recibo de entrega enviado para impressao"
        );
    }

    @PostMapping("/test")
    public Map<String, Object> printTest(@RequestBody PrintTestRequest request) {
        printerService.printTestPage(request);

        return Map.of(
                "success", true,
                "message", "Teste enviado para impressao"
        );
    }
}
