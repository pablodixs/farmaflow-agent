package com.scriptles.farmaflowagent.service;

import com.scriptles.farmaflowagent.dto.PdfPrintStartResponse;
import com.scriptles.farmaflowagent.dto.PdfPrintStatusResponse;
import org.apache.pdfbox.Loader;
import org.apache.pdfbox.pdmodel.PDDocument;
import org.apache.pdfbox.printing.PDFPageable;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.web.multipart.MultipartFile;
import org.springframework.web.server.ResponseStatusException;

import javax.print.PrintService;
import javax.print.PrintServiceLookup;
import java.awt.print.PrinterJob;
import java.io.IOException;
import java.time.Duration;
import java.time.Instant;
import java.util.Arrays;
import java.util.Objects;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

@Service
public class PdfPrintJobService {

    private static final Duration JOB_RETENTION = Duration.ofMinutes(30);
    private static final byte[] PDF_SIGNATURE = new byte[]{'%', 'P', 'D', 'F'};

    private final ConcurrentHashMap<String, PdfPrintJob> jobs = new ConcurrentHashMap<>();
    private final ExecutorService executorService = Executors.newSingleThreadExecutor(runnable -> {
        Thread thread = new Thread(runnable, "pdf-print-worker");
        thread.setDaemon(true);
        return thread;
    });

    public PdfPrintStartResponse start(
            MultipartFile file,
            String printerName,
            String paperWidth,
            String type
    ) {
        cleanupOldJobs();
        validateInput(file, printerName);

        byte[] data = readPdf(file);
        String jobId = UUID.randomUUID().toString();
        PdfPrintJob job = new PdfPrintJob(
                jobId,
                printerName.trim(),
                Objects.requireNonNullElse(file.getOriginalFilename(), "document.pdf"),
                type,
                paperWidth
        );

        jobs.put(jobId, job);
        executorService.submit(() -> print(job, data));

        return new PdfPrintStartResponse(
                jobId,
                job.status.name(),
                job.progress,
                job.message,
                "/print/pdf/" + jobId + "/status",
                job.error
        );
    }

    public PdfPrintStatusResponse status(String jobId) {
        PdfPrintJob job = jobs.get(jobId);

        if (job == null) {
            throw new ResponseStatusException(
                    HttpStatus.NOT_FOUND,
                    "Job de impressao PDF nao encontrado: " + jobId
            );
        }

        return job.toResponse();
    }

    private void print(PdfPrintJob job, byte[] data) {
        try {
            job.update(PdfPrintStatus.PROCESSING, 15, "PDF recebido e validado", null);

            PrintService printer = findPrinter(job.printerName);
            job.update(PdfPrintStatus.PROCESSING, 35, "Impressora localizada", null);

            try (PDDocument document = Loader.loadPDF(data)) {
                PrinterJob printJob = PrinterJob.getPrinterJob();
                printJob.setPrintService(printer);
                printJob.setJobName(job.fileName);
                printJob.setPageable(new PDFPageable(document));

                job.update(PdfPrintStatus.PRINTING, 70, "Renderizando e enviando PDF para impressora", null);
                printJob.print();
            }

            job.update(PdfPrintStatus.COMPLETED, 100, "PDF enviado para impressao", null);
        } catch (Exception exception) {
            job.update(PdfPrintStatus.FAILED, 100, "Falha ao imprimir PDF", errorMessage(exception));
        }
    }

    private String errorMessage(Exception exception) {
        String message = exception.getMessage();

        if (message == null || message.isBlank()) {
            return exception.getClass().getSimpleName();
        }

        return message;
    }

    private PrintService findPrinter(String printerName) {
        PrintService[] printServices = PrintServiceLookup.lookupPrintServices(null, null);

        return Arrays.stream(printServices)
                .filter(printer -> printer.getName().equalsIgnoreCase(printerName))
                .findFirst()
                .orElseThrow(() -> new ResponseStatusException(
                        HttpStatus.BAD_REQUEST,
                        "Impressora nao encontrada: " + printerName
                ));
    }

    private void validateInput(MultipartFile file, String printerName) {
        if (file == null || file.isEmpty()) {
            throw new ResponseStatusException(HttpStatus.BAD_REQUEST, "Envie um arquivo PDF");
        }

        if (printerName == null || printerName.isBlank()) {
            throw new ResponseStatusException(HttpStatus.BAD_REQUEST, "Informe printerName");
        }
    }

    private byte[] readPdf(MultipartFile file) {
        try {
            byte[] data = file.getBytes();

            if (!hasPdfSignature(data)) {
                throw new ResponseStatusException(HttpStatus.BAD_REQUEST, "O arquivo enviado nao parece ser um PDF");
            }

            return data;
        } catch (IOException exception) {
            throw new ResponseStatusException(HttpStatus.BAD_REQUEST, "Nao foi possivel ler o PDF", exception);
        }
    }

    private boolean hasPdfSignature(byte[] data) {
        if (data.length < PDF_SIGNATURE.length) {
            return false;
        }

        for (int index = 0; index < PDF_SIGNATURE.length; index++) {
            if (data[index] != PDF_SIGNATURE[index]) {
                return false;
            }
        }

        return true;
    }

    private void cleanupOldJobs() {
        Instant threshold = Instant.now().minus(JOB_RETENTION);
        jobs.entrySet().removeIf(entry -> entry.getValue().updatedAt.isBefore(threshold));
    }

    private enum PdfPrintStatus {
        QUEUED,
        PROCESSING,
        PRINTING,
        COMPLETED,
        FAILED
    }

    private static final class PdfPrintJob {
        private final String jobId;
        private final String printerName;
        private final String fileName;
        private final String type;
        private final String paperWidth;
        private volatile PdfPrintStatus status = PdfPrintStatus.QUEUED;
        private volatile int progress = 0;
        private volatile String message = "PDF aguardando processamento";
        private volatile String error;
        private volatile Instant updatedAt = Instant.now();

        private PdfPrintJob(
                String jobId,
                String printerName,
                String fileName,
                String type,
                String paperWidth
        ) {
            this.jobId = jobId;
            this.printerName = printerName;
            this.fileName = fileName;
            this.type = type;
            this.paperWidth = paperWidth;
        }

        private void update(PdfPrintStatus status, int progress, String message, String error) {
            this.status = status;
            this.progress = progress;
            this.message = message;
            this.error = error;
            this.updatedAt = Instant.now();
        }

        private PdfPrintStatusResponse toResponse() {
            return new PdfPrintStatusResponse(
                    jobId,
                    status.name(),
                    progress,
                    message,
                    printerName,
                    fileName,
                    type,
                    paperWidth,
                    error
            );
        }
    }
}
