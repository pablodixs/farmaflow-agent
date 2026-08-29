using FarmaFlow.Server.Host;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "FarmaFlow Server");

string localConfig = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "FarmaFlow", "Server", "appsettings.local.json");
builder.Configuration.AddJsonFile(localConfig, optional: true, reloadOnChange: false);

var options = builder.Configuration.GetSection("ServerHost").Get<ServerHostOptions>() ?? new ServerHostOptions();
Directory.CreateDirectory(options.DataDirectory);
builder.Logging.AddProvider(new DailyFileLoggerProvider(Path.Combine(options.DataDirectory, "logs")));
var certificate = LocalCertificateProvider.LoadOrCreate();
var secrets = ServerSecrets.Load(options);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(secrets);
builder.Services.AddSingleton<ProcessSupervisorService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ProcessSupervisorService>());
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(services => services.GetRequiredService<BackupService>());
builder.Services.AddHttpClient("reverse-proxy", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.WebHost.ConfigureKestrel(server => server.ListenAnyIP(
    options.PublicPort,
    listen => listen.UseHttps(certificate)));

var app = builder.Build();
if (args.Contains("--backup-once", StringComparer.OrdinalIgnoreCase))
{
    await app.Services.GetRequiredService<BackupService>().CreateBackupAsync(CancellationToken.None);
    return;
}
app.MapGet("/.well-known/farmaflow/server", () => Results.Ok(new
{
    serverId = Environment.MachineName,
    version = options.Version,
    certificateSha256 = LocalCertificateProvider.Sha256(certificate)
}));
app.MapGet("/.well-known/farmaflow/health", (ProcessSupervisorService supervisor) => Results.Ok(new
{
    status = supervisor.BackendRunning && supervisor.WebRunning ? "UP" : "DEGRADED",
    version = options.Version,
    internet = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(),
    serverTime = DateTimeOffset.Now,
    components = new { backend = supervisor.BackendRunning, web = supervisor.WebRunning, postgres = supervisor.BackendRunning }
}));

app.Run(async context =>
{
    bool backend = context.Request.Path.StartsWithSegments("/backend", out PathString remaining);
    string baseUrl = backend ? options.BackendUrl : options.WebUrl;
    string path = backend ? remaining.Value ?? "/" : context.Request.Path.Value ?? "/";
    var target = new Uri($"{baseUrl.TrimEnd('/')}{path}{context.Request.QueryString}");
    var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("reverse-proxy");
    await HttpReverseProxy.ForwardAsync(context, client, target, backend, context.RequestAborted);
});

await app.RunAsync();
