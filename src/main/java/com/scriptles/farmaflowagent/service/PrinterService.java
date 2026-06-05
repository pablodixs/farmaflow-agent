package com.scriptles.farmaflowagent.service;

import com.scriptles.farmaflowagent.dto.PrintReceiptRequest;
import com.scriptles.farmaflowagent.dto.PrintTestRequest;
import org.springframework.stereotype.Service;

import javax.print.Doc;
import javax.print.DocFlavor;
import javax.print.DocPrintJob;
import javax.print.PrintException;
import javax.print.PrintService;
import javax.print.PrintServiceLookup;
import javax.print.SimpleDoc;
import java.util.Arrays;
import java.util.List;

@Service
public class PrinterService {

    private final EscPosBuilder escPosBuilder;

    public PrinterService(EscPosBuilder escPosBuilder) {
        this.escPosBuilder = escPosBuilder;
    }

    public List<String> listPrinters() {
        PrintService[] printServices = PrintServiceLookup.lookupPrintServices(null, null);

        return Arrays.stream(printServices)
                .map(PrintService::getName)
                .toList();
    }

    public void printReceipt(PrintReceiptRequest request) {
        PrintService printer = findPrinter(request.printerName());
        byte[] data = escPosBuilder.build(request);

        print(printer, data);
    }

    public void printTestPage(PrintTestRequest request) {
        PrintService printer = findPrinter(request.printerName());
        byte[] data = escPosBuilder.buildTestPage(request);

        print(printer, data);
    }

    private void print(PrintService printer, byte[] data) {
        DocPrintJob job = printer.createPrintJob();

        Doc doc = new SimpleDoc(
                data,
                DocFlavor.BYTE_ARRAY.AUTOSENSE,
                null
        );

        try {
            job.print(doc, null);
        } catch (PrintException exception) {
            throw new RuntimeException("Erro ao imprimir cupom", exception);
        }
    }

    private PrintService findPrinter(String printerName) {
        PrintService[] printServices = PrintServiceLookup.lookupPrintServices(null, null);

        return Arrays.stream(printServices)
                .filter(printer -> printer.getName().equalsIgnoreCase(printerName))
                .findFirst()
                .orElseThrow(() -> new RuntimeException(
                        "Impressora nao encontrada: " + printerName
                ));
    }
}
