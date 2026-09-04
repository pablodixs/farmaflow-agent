using FarmaFlow.Migration.Core;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FarmaFlow.Server.Host;

public sealed class BackupService(
    ServerHostOptions options,
    ServerSecrets secrets,
    ILogger<BackupService> logger) : BackgroundService
{
    private const int FormatVersion = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeOnly backupTime = TimeOnly.TryParseExact(options.BackupTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed)
            ? parsed
            : new TimeOnly(2, 0);

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset next = new(now.Year, now.Month, now.Day, backupTime.Hour, backupTime.Minute, 0, now.Offset);
            if (next <= now) next = next.AddDays(1);
            await Task.Delay(next - now, stoppingToken);
            try
            {
                await CreateBackupAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "O backup diário falhou.");
            }
        }
    }

    public async Task<string> CreateBackupAsync(CancellationToken cancellationToken)
    {
        string backupDirectory = Path.Combine(options.DataDirectory, "backups");
        string temporaryDirectory = Path.Combine(options.DataDirectory, "temporary");
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(temporaryDirectory);

        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string dumpPath = Path.Combine(temporaryDirectory, $"farmaflow-{stamp}.dump");
        string backupPath = Path.Combine(backupDirectory, $"farmaflow-{stamp}.ffbackup");
        try
        {
            await RunPostgresToolAsync(
                options.ResolveRuntimePath("postgres", "bin", "pg_dump.exe"),
                $"--host=127.0.0.1 --port=54329 --username=farmaflow --dbname=farmaflow --format=custom --no-owner --no-acl --file=\"{dumpPath}\"",
                cancellationToken);

            string catalog = await RunPostgresToolAsync(
                options.ResolveRuntimePath("postgres", "bin", "pg_restore.exe"),
                $"--list \"{dumpPath}\"",
                cancellationToken);
            if (string.IsNullOrWhiteSpace(catalog)) throw new InvalidOperationException("O catálogo do pg_restore está vazio.");

            string plaintextSha256 = await ComputeSha256Async(dumpPath, cancellationToken);
            var manifest = new
            {
                format = "FarmaFlow encrypted PostgreSQL backup",
                formatVersion = FormatVersion,
                kind = "SERVER_BACKUP",
                createdAt = DateTimeOffset.Now,
                database = "farmaflow",
                databaseMajorVersion = 17,
                plaintextSha256,
                pgRestoreCatalogSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(catalog)))
            };
            using JsonDocument embeddedManifest = JsonDocument.Parse(JsonSerializer.Serialize(manifest));
            string packageSha256 = await PackageEnvelope.WriteV3Async(backupPath, dumpPath, embeddedManifest.RootElement, secrets.BackupKey, cancellationToken);
            JsonObject sidecar = JsonNode.Parse(embeddedManifest.RootElement.GetRawText())!.AsObject();
            sidecar["encryptedBytes"] = new FileInfo(backupPath).Length;
            sidecar["packageSha256"] = packageSha256;
            string manifestPath = $"{backupPath}.json";
            await WriteTextAtomicallyAsync(manifestPath, sidecar.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

            if (!string.IsNullOrWhiteSpace(options.ExternalBackupDirectory))
            {
                Directory.CreateDirectory(options.ExternalBackupDirectory);
                await CopyAtomicallyAsync(backupPath, Path.Combine(options.ExternalBackupDirectory, Path.GetFileName(backupPath)), cancellationToken);
                await CopyAtomicallyAsync(manifestPath, Path.Combine(options.ExternalBackupDirectory, Path.GetFileName(manifestPath)), cancellationToken);
                ApplyRetention(options.ExternalBackupDirectory);
            }

            ApplyRetention(backupDirectory);
            logger.LogInformation("Backup {BackupPath} criado e validado.", backupPath);
            return backupPath;
        }
        finally
        {
            if (File.Exists(dumpPath)) File.Delete(dumpPath);
        }
    }

    private async Task<string> RunPostgresToolAsync(string executable, string arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Ferramenta PostgreSQL não encontrada.", executable);
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["PGPASSWORD"] = secrets.DatabasePassword;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Não foi possível executar {Path.GetFileName(executable)}.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            }
            throw;
        }
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} falhou: {error}");
        return output;
    }

    private static void ApplyRetention(string directory)
    {
        FileInfo[] backups = new DirectoryInfo(directory).GetFiles("*.ffbackup")
            .OrderByDescending(file => file.CreationTimeUtc)
            .ToArray();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo file in backups.Take(14)) keep.Add(file.FullName);
        foreach (FileInfo file in backups
            .GroupBy(file => ISOWeek.GetYear(file.CreationTimeUtc) * 100 + ISOWeek.GetWeekOfYear(file.CreationTimeUtc))
            .Take(8)
            .Select(group => group.First())) keep.Add(file.FullName);
        foreach (FileInfo file in backups
            .GroupBy(file => file.CreationTimeUtc.Year * 100 + file.CreationTimeUtc.Month)
            .Take(12)
            .Select(group => group.First())) keep.Add(file.FullName);

        foreach (FileInfo file in backups.Where(file => !keep.Contains(file.FullName)))
        {
            file.Delete();
            string manifest = $"{file.FullName}.json";
            if (File.Exists(manifest)) File.Delete(manifest);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
    }

    private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task CopyAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
