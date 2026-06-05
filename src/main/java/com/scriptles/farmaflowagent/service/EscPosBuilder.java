package com.scriptles.farmaflowagent.service;

import com.scriptles.farmaflowagent.dto.PrintReceiptRequest;
import com.scriptles.farmaflowagent.dto.PrintTestRequest;
import org.springframework.stereotype.Component;

import java.io.ByteArrayOutputStream;
import java.math.BigDecimal;
import java.nio.charset.Charset;
import java.text.NumberFormat;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.Locale;

@Component
public class EscPosBuilder {

    private static final Charset CHARSET = Charset.forName("CP850");

    private static final byte ESC = 0x1B;
    private static final byte GS = 0x1D;

    private static final int WIDTH_58MM = 32;
    private static final int WIDTH_80MM = 48;

    public byte[] buildTestPage(PrintTestRequest request) {
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            int width = paperWidth(request.paperWidth());

            initialize(out);

            center(out);
            boldOn(out);
            writeLine(out, "FARMAFLOW AGENT");
            boldOff(out);
            separator(out, width);

            writeLine(out, "TESTE DE IMPRESSAO");
            writeLine(out, "Data: " + LocalDateTime.now()
                    .format(DateTimeFormatter.ofPattern("dd/MM/yyyy HH:mm")));
            writeLine(out, "Impressora: " + safe(request.printerName()));
            writeLine(out, "Papel: " + safe(request.paperWidth()));
            separator(out, width);

            left(out);
            writeLine(out, "Se esta mensagem foi impressa,");
            writeLine(out, "a comunicacao esta funcionando.");
            separator(out, width);

            writeColumns(out, "Coluna A", "Coluna B", width);
            writeColumns(out, "Produto teste", "R$ 1,00", width);

            feed(out, 4);
            cut(out);

            return out.toByteArray();
        } catch (Exception exception) {
            throw new RuntimeException("Erro ao montar teste ESC/POS", exception);
        }
    }

    public byte[] build(PrintReceiptRequest request) {
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();

            int width = paperWidth(request.paperWidth());

            initialize(out);

            center(out);
            boldOn(out);
            writeLine(out, safe(request.store().name()));
            boldOff(out);

            writeLine(out, "CNPJ: " + safe(request.store().cnpj()));
            writeLine(out, safe(request.store().address()));
            separator(out, width);

            left(out);

            if ("delivery_receipt".equalsIgnoreCase(request.type())) {
                buildDeliveryHeader(out, request, width);
            } else {
                buildSaleHeader(out, width);
            }

            separator(out, width);

            for (PrintReceiptRequest.ItemDto item : request.items()) {
                writeItem(out, item, width);
            }

            separator(out, width);

            boldOn(out);
            writeColumns(
                    out,
                    "TOTAL",
                    money(request.total()),
                    width
            );
            boldOff(out);

            writeLine(out, "Pagamento: " + safe(request.paymentMethod()));

            separator(out, width);

            center(out);
            writeLine(out, "Obrigado pela preferencia!");
            writeLine(out, "");

            if ("delivery_receipt".equalsIgnoreCase(request.type())) {
                left(out);
                writeLine(out, "");
                writeLine(out, "Assinatura:");
                writeLine(out, "");
                writeLine(out, "____________________________");
            }

            feed(out, 4);
            cut(out);

            return out.toByteArray();
        } catch (Exception exception) {
            throw new RuntimeException("Erro ao montar ESC/POS", exception);
        }
    }

    private void buildSaleHeader(ByteArrayOutputStream out, int width) {
        writeLine(out, "CUPOM DE VENDA");
        writeLine(out, "Data: " + LocalDateTime.now()
                .format(DateTimeFormatter.ofPattern("dd/MM/yyyy HH:mm")));
    }

    private void buildDeliveryHeader(
            ByteArrayOutputStream out,
            PrintReceiptRequest request,
            int width
    ) {
        writeLine(out, "RECIBO DE ENTREGA");
        writeLine(out, "Data: " + LocalDateTime.now()
                .format(DateTimeFormatter.ofPattern("dd/MM/yyyy HH:mm")));

        if (request.customer() != null) {
            separator(out, width);
            writeLine(out, "Cliente: " + safe(request.customer().name()));
            writeLine(out, "Telefone: " + safe(request.customer().phone()));
            writeWrapped(out, "Endereco: " + safe(request.customer().address()), width);
        }
    }

    private void writeItem(
            ByteArrayOutputStream out,
            PrintReceiptRequest.ItemDto item,
            int width
    ) {
        String quantity = item.quantity() + "x ";
        String name = quantity + safe(item.name());

        writeWrapped(out, name, width);
        writeColumns(out, money(item.unitPrice()), money(item.total()), width);
    }

    private int paperWidth(String paperWidth) {
        return "58mm".equalsIgnoreCase(paperWidth)
                ? WIDTH_58MM
                : WIDTH_80MM;
    }

    private void initialize(ByteArrayOutputStream out) {
        out.write(ESC);
        out.write('@');
    }

    private void center(ByteArrayOutputStream out) {
        out.write(ESC);
        out.write('a');
        out.write(1);
    }

    private void left(ByteArrayOutputStream out) {
        out.write(ESC);
        out.write('a');
        out.write(0);
    }

    private void boldOn(ByteArrayOutputStream out) {
        out.write(ESC);
        out.write('E');
        out.write(1);
    }

    private void boldOff(ByteArrayOutputStream out) {
        out.write(ESC);
        out.write('E');
        out.write(0);
    }

    private void cut(ByteArrayOutputStream out) {
        out.write(GS);
        out.write('V');
        out.write(0);
    }

    private void feed(ByteArrayOutputStream out, int lines) {
        for (int i = 0; i < lines; i++) {
            writeLine(out, "");
        }
    }

    private void separator(ByteArrayOutputStream out, int width) {
        writeLine(out, "-".repeat(width));
    }

    private void writeColumns(
            ByteArrayOutputStream out,
            String left,
            String right,
            int width
    ) {
        left = safe(left);
        right = safe(right);

        int spaces = width - left.length() - right.length();

        if (spaces < 1) {
            spaces = 1;
        }

        writeLine(out, left + " ".repeat(spaces) + right);
    }

    private void writeWrapped(ByteArrayOutputStream out, String text, int width) {
        text = safe(text);

        while (text.length() > width) {
            writeLine(out, text.substring(0, width));
            text = text.substring(width);
        }

        if (!text.isBlank()) {
            writeLine(out, text);
        }
    }

    private void writeLine(ByteArrayOutputStream out, String text) {
        try {
            out.write(safe(text).getBytes(CHARSET));
            out.write('\n');
        } catch (Exception exception) {
            throw new RuntimeException(exception);
        }
    }

    private String safe(String value) {
        return value == null ? "" : value;
    }

    private String money(BigDecimal value) {
        if (value == null) {
            value = BigDecimal.ZERO;
        }

        return NumberFormat
                .getCurrencyInstance(Locale.of("pt", "BR"))
                .format(value);
    }
}
