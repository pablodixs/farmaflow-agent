using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FarmaFlow.Migration.Desktop;

internal sealed class LocalPostgresCluster : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _postgresBin;
    private readonly string _password;
    private readonly int _port;

    private LocalPostgresCluster(string root, string postgresBin, string password, int port)
    {
        _root = root;
        _postgresBin = postgresBin;
        _password = password;
        _port = port;
    }

    internal int Port => _port;
    internal string Password => _password;
    internal string PostgresBin => _postgresBin;

    internal static async Task<LocalPostgresCluster> StartAsync(string postgresBin, string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36));
        int port = FindFreePort();
        // initdb refuses to initialize a non-empty PGDATA directory. Keep the
        // one-time password file outside the cluster directory.
        string passwordFile = Path.Combine(Path.GetTempPath(), $"farmaflow-initdb-{Guid.NewGuid():N}.tmp");
        bool started = false;
        await File.WriteAllTextAsync(passwordFile, password, Encoding.ASCII, cancellationToken);
        try
        {
            ProcessResult init = await ProcessRunner.RunAsync(
                Path.Combine(postgresBin, "initdb.exe"),
                ["--pgdata", root, "--username", "farmaflow", "--pwfile", passwordFile, "--encoding", "UTF8", "--locale", "C"],
                cancellationToken: cancellationToken);
            if (init.ExitCode != 0) throw new InvalidOperationException($"Não foi possível preparar o PostgreSQL temporário: {init.Error}");
            await File.AppendAllTextAsync(Path.Combine(root, "postgresql.conf"), $"\nlisten_addresses = '127.0.0.1'\nport = {port}\npassword_encryption = 'scram-sha-256'\n", cancellationToken);
            // PostgreSQL's HBA parser does not ignore a UTF-8 BOM and reports
            // the first connection type as "?local". Always write this config
            // as UTF-8 without BOM on Windows.
            await File.WriteAllTextAsync(
                Path.Combine(root, "pg_hba.conf"),
                "local all all scram-sha-256\nhost all all 127.0.0.1/32 scram-sha-256\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            string serverLog = Path.Combine(root, "postgres.log");
            ProcessResult start = await ProcessRunner.RunAsync(
                Path.Combine(postgresBin, "pg_ctl.exe"),
                ["start", "-D", root, "-w", "-t", "60", "-l", serverLog, "-o", $"-p {port}"],
                cancellationToken: cancellationToken);
            if (start.ExitCode != 0)
            {
                string details = File.Exists(serverLog) ? await File.ReadAllTextAsync(serverLog, cancellationToken) : start.Error;
                throw new InvalidOperationException($"Não foi possível iniciar o PostgreSQL temporário: {details}");
            }
            started = true;
            return new LocalPostgresCluster(root, postgresBin, password, port);
        }
        catch
        {
            if (started)
            {
                try { await ProcessRunner.RunAsync(Path.Combine(postgresBin, "pg_ctl.exe"), ["stop", "--pgdata", root, "--wait"], cancellationToken: CancellationToken.None); } catch { }
            }
            throw;
        }
        finally
        {
            if (File.Exists(passwordFile)) File.Delete(passwordFile);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            await ProcessRunner.RunAsync(Path.Combine(_postgresBin, "pg_ctl.exe"), ["stop", "--pgdata", _root, "--wait"], cancellationToken: CancellationToken.None);
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
