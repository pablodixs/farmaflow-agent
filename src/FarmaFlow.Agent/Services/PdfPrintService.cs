using System.Collections.Concurrent;
using PdfiumPrinter;

namespace FarmaFlow.Agent.Services;

public sealed record PrintJobStatus(string JobId, string Status, int Progress, string Message, string? Error);

public sealed class PdfPrintService(PrintingService printing)
{
    private readonly ConcurrentDictionary<string, PrintJobStatus> _jobs = new();

    public async Task<PrintJobStatus> StartAsync(IFormFile file, string printerName)
    {
        if (!printing.Printers().Contains(printerName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Impressora não encontrada: {printerName}");
        var id = Guid.NewGuid().ToString();
        var queued = new PrintJobStatus(id, "QUEUED", 0, "PDF aguardando processamento", null);
        _jobs[id] = queued;
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer);
        var data = buffer.ToArray();
        _ = Task.Run(() => Print(id, data, printerName));
        return queued;
    }

    public PrintJobStatus? Get(string id) => _jobs.GetValueOrDefault(id);

    private async Task Print(string id, byte[] data, string printerName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"farmaflow-{id}.pdf");
        try
        {
            _jobs[id] = new(id, "PROCESSING", 25, "Preparando PDF", null);
            await File.WriteAllBytesAsync(path, data);
            _jobs[id] = new(id, "PRINTING", 70, "Enviando PDF para a impressora", null);
            // Print through PDFium instead of the Windows `printto` shell verb.
            // `printto` depends on whichever PDF viewer happens to be installed
            // and often exits successfully without creating a spooler job.
            var printer = new PdfPrinter(printerName);
            await Task.Run(() => printer.Print(path));
            _jobs[id] = new(id, "COMPLETED", 100, "PDF enviado para a fila de impressão", null);
        }
        catch (Exception exception)
        {
            _jobs[id] = new(id, "FAILED", 100, "Falha ao imprimir PDF", exception.Message);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
