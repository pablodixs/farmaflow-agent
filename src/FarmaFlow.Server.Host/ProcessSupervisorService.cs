using System.Diagnostics;

namespace FarmaFlow.Server.Host;

public sealed class ProcessSupervisorService(
    ServerHostOptions options,
    ServerSecrets secrets,
    ILogger<ProcessSupervisorService> logger) : BackgroundService
{
    private readonly List<Process> _processes = [];
    private volatile bool _backendRunning;
    private volatile bool _webRunning;

    public bool BackendRunning => _backendRunning;
    public bool WebRunning => _webRunning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            StartMissingProcesses();
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (Process process in _processes.Where(process => !process.HasExited))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Não foi possível encerrar {ProcessName}.", process.ProcessName);
            }
        }
        return base.StopAsync(cancellationToken);
    }

    private void StartMissingProcesses()
    {
        _processes.RemoveAll(process => process.HasExited);
        if (_processes.All(process => process.StartInfo.Environment["FARMAFLOW_COMPONENT"] != "backend"))
            _processes.Add(StartBackend());
        if (_processes.All(process => process.StartInfo.Environment["FARMAFLOW_COMPONENT"] != "web"))
            _processes.Add(StartWeb());
    }

    private Process StartBackend()
    {
        string java = options.ResolveRuntimePath("java", "bin", "java.exe");
        string jar = options.ResolveRuntimePath("backend", "app.jar");
        var environment = new Dictionary<string, string?>
        {
            ["FARMAFLOW_COMPONENT"] = "backend",
            ["SPRING_PROFILES_ACTIVE"] = "local",
            ["DATABASE_URL"] = "jdbc:postgresql://127.0.0.1:54329/farmaflow",
            ["DATABASE_USERNAME"] = "farmaflow",
            ["DATABASE_PASSWORD"] = secrets.DatabasePassword,
            ["JWT_SECRET"] = secrets.JwtSecret,
            ["FARMAFLOW_VERSION"] = options.Version,
            ["SERVER_PORT"] = "8180"
        };
        return Start("backend", java, $"-jar \"{jar}\"", Path.GetDirectoryName(jar)!, environment);
    }

    private Process StartWeb()
    {
        string node = options.ResolveRuntimePath("node", "node.exe");
        string server = options.ResolveRuntimePath("web", "server.js");
        var environment = new Dictionary<string, string?>
        {
            ["FARMAFLOW_COMPONENT"] = "web",
            ["PORT"] = "3100",
            ["HOSTNAME"] = "127.0.0.1",
            ["API_INTERNAL_URL"] = options.BackendUrl,
            ["NEXTAUTH_URL_INTERNAL"] = options.WebUrl,
            ["NEXTAUTH_SECRET"] = secrets.NextAuthSecret
        };
        return Start("web", node, $"\"{server}\"", Path.GetDirectoryName(server)!, environment);
    }

    private Process Start(string name, string executable, string arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException($"Runtime de {name} não encontrado.", executable);
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var item in environment) startInfo.Environment[item.Key] = item.Value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) logger.LogInformation("[{Name}] {Line}", name, eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) logger.LogWarning("[{Name}] {Line}", name, eventArgs.Data); };
        process.Exited += (_, _) =>
        {
            if (name == "backend") _backendRunning = false;
            if (name == "web") _webRunning = false;
            logger.LogError("O processo {Name} foi encerrado com código {ExitCode}.", name, process.ExitCode);
        };
        if (!process.Start()) throw new InvalidOperationException($"Não foi possível iniciar {name}.");
        if (name == "backend") _backendRunning = true;
        if (name == "web") _webRunning = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        logger.LogInformation("Processo {Name} iniciado com PID {Pid}.", name, process.Id);
        return process;
    }
}
