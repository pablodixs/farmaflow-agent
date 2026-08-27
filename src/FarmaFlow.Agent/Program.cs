using FarmaFlow.Agent;
using FarmaFlow.Agent.Infrastructure;
using FarmaFlow.Agent.Services;
using Microsoft.AspNetCore.Http.Features;
using System.Runtime.InteropServices;

try
{
    ApplicationConfiguration.Initialize();
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    Application.ThreadException += (_, eventArgs) =>
        StartupDiagnostics.ReportFatal(eventArgs.Exception, "Falha não tratada na interface do agente.");
    AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    {
        if (eventArgs.ExceptionObject is Exception exception)
            StartupDiagnostics.ReportFatal(exception, "Falha não tratada no agente.");
    };

    var builder = WebApplication.CreateBuilder(args);
    var options = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
    var connections = new DesktopConnectionStore(options);
    builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(connections);
    builder.Services.AddSingleton<AgentStore>();
    builder.Services.AddSingleton<LocalAccessService>();
    builder.Services.AddSingleton<PrintingService>();
    builder.Services.AddSingleton<PdfPrintService>();
    builder.Services.AddSingleton(new HttpClient(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = connections.IsValidCertificate
    }));
    builder.Services.AddSingleton<PairingService>();
    builder.Services.AddHostedService<HeartbeatWorker>();
    builder.Services.Configure<FormOptions>(value => value.MultipartBodyLengthLimit = 30 * 1024 * 1024);
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(connections.IsAllowedOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()));

    var app = builder.Build();
    app.UseCors();

    var access = app.Services.GetRequiredService<LocalAccessService>();
    app.Use(async (context, next) =>
    {
        var publicPath = context.Request.Path == "/agent/health" || context.Request.Path == "/agent/local/handshake";
        if (!publicPath && !access.IsValid(context.Request.Headers.Authorization))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    });

app.MapGet("/agent/health", (AgentStore store) => Results.Ok(new
{
    status = "running",
    version = PairingService.Version,
    paired = store.GetRegistration() is not null,
    pendingOperations = store.PendingCount()
}));
app.MapGet("/agent/local/handshake", (LocalAccessService local) => Results.Ok(new { challenge = local.CreateChallenge() }));
app.MapPost("/agent/local/handshake", (HandshakeRequest request, LocalAccessService local) =>
{
    try { return Results.Ok(new { token = local.Exchange(request.Challenge), expiresInSeconds = 43_200 }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
});
app.MapGet("/agent/status", (AgentStore store) => Results.Ok(new
{
    operatingSystem = new { name = Environment.OSVersion.Platform.ToString(), version = Environment.OSVersion.VersionString, architecture = RuntimeInformation.OSArchitecture.ToString() },
    hardware = new
    {
        computerName = Environment.MachineName,
        availableProcessors = Environment.ProcessorCount,
        maxMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        totalMemoryBytes = GC.GetTotalMemory(false),
        freeMemoryBytes = Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes - GC.GetTotalMemory(false))
    },
    internet = new { connected = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(), checkedUrl = options.ApiBaseUrl, latencyMs = 0, error = (string?)null },
    connection = new { paired = store.GetRegistration() is not null, pendingOperations = store.PendingCount() }
}));
app.MapGet("/print/printers", (PrintingService printing) => printing.Printers());
app.MapPost("/print/test", (PrintTestRequest request, PrintingService printing) =>
{
    var content = System.Text.Encoding.GetEncoding(850).GetBytes("\u001b@\u001ba\u0001FARMAFLOW AGENT\nTESTE DE IMPRESSAO\n\n\n\u001dV\0");
    printing.PrintRaw(request.PrinterName, content);
    return Results.Ok(new { success = true });
});
app.MapPost("/print/pdf", async (HttpRequest request, PdfPrintService pdf) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var printerName = form["printerName"].ToString();
    if (file is null || file.Length == 0 || string.IsNullOrWhiteSpace(printerName)) return Results.BadRequest(new { message = "Arquivo e impressora são obrigatórios." });
    var job = await pdf.StartAsync(file, printerName);
    return Results.Accepted($"/print/pdf/{job.JobId}/status", new { jobId = job.JobId, job.Status, job.Progress, job.Message, statusUrl = $"/print/pdf/{job.JobId}/status", job.Error });
});
app.MapGet("/print/pdf/{jobId}/status", (string jobId, PdfPrintService pdf) =>
    pdf.Get(jobId) is { } job ? Results.Ok(job) : Results.NotFound());
app.MapPost("/offline/operations", (OfflineOperation request, AgentStore store) => { store.Enqueue(request.Type, request.Payload); return Results.Accepted(value: new { queued = true }); });

    await app.StartAsync();
    if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
    {
        using var smokeClient = new HttpClient();
        using var smokeResponse = await smokeClient.GetAsync($"http://127.0.0.1:{options.Port}/agent/health");
        smokeResponse.EnsureSuccessStatusCode();
        await app.StopAsync();
        return;
    }

    var tray = new TrayApplicationContext(
        app.Services.GetRequiredService<DesktopConnectionStore>(),
        app.Services.GetRequiredService<PairingService>(),
        app.Services.GetRequiredService<AgentStore>());
    Application.Run(tray);
    await app.StopAsync();
}
catch (Exception exception)
{
    if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
    {
        StartupDiagnostics.Write(exception, "Falha no smoke test do FarmaFlow Agent.");
        Environment.ExitCode = 1;
    }
    else
    {
        StartupDiagnostics.ReportFatal(exception, "Falha ao iniciar o FarmaFlow Agent.");
    }
}

internal sealed record HandshakeRequest(string Challenge);
internal sealed record PrintTestRequest(string PrinterName, string? PaperWidth);
internal sealed record OfflineOperation(string Type, object Payload);
