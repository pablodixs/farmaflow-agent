using FarmaFlow.Migration.Core;
using Npgsql;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FarmaFlow.Migration.Desktop;

internal sealed record MigrationSource(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    string SslMode,
    SupabaseProjectAddress Project);
internal sealed record StoreChoice(Guid Id, string Name, Guid OrganizationId);
internal sealed record MigrationRequest(
    MigrationSource Source,
    string PostgresBin,
    string OutputDirectory,
    string PackagePassword,
    IReadOnlyList<StoreChoice> Stores,
    bool FinalCutover,
    bool MaintenanceConfirmed,
    bool DataApiConfirmed,
    string PublicApiKey);
internal sealed record MigrationReportFile(string Name, long Bytes, string Sha256);

internal sealed class MigrationPipeline
{
    private readonly string _migrationExecutable;

    internal MigrationPipeline(string migrationExecutable) => _migrationExecutable = migrationExecutable;

    internal static void ValidateRuntime(string postgresBin)
    {
        string[] required = ["initdb.exe", "pg_ctl.exe", "createdb.exe", "dropdb.exe", "pg_dump.exe", "pg_restore.exe"];
        string[] missing = required.Where(name => !File.Exists(Path.Combine(postgresBin, name))).ToArray();
        if (missing.Length != 0) throw new InvalidOperationException($"PostgreSQL 17 portátil incompleto. Faltam: {string.Join(", ", missing)}.");
    }

    internal async Task<IReadOnlyList<StoreChoice>> DiscoverStoresAsync(MigrationSource source, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = source.Host, Port = source.Port, Database = source.Database, Username = source.Username,
            Password = source.Password, SslMode = Enum.Parse<SslMode>(source.SslMode, ignoreCase: true), Timeout = 30
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var schema = new NpgsqlCommand("SELECT COALESCE((SELECT version FROM public.flyway_schema_history WHERE success ORDER BY installed_rank DESC LIMIT 1), '0')", connection))
        {
            string version = Convert.ToString(await schema.ExecuteScalarAsync(cancellationToken)) ?? "0";
            if (!int.TryParse(version, out int number) || number < 52)
                throw new InvalidOperationException($"O Supabase está no schema V{version}; é necessário Flyway V52 ou superior.");
        }
        await using var command = new NpgsqlCommand("SELECT id,name,organization_id FROM public.stores ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var stores = new List<StoreChoice>();
        while (await reader.ReadAsync(cancellationToken)) stores.Add(new StoreChoice(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2)));
        return stores;
    }

    internal async Task HardenDataApiAsync(MigrationSource source, string publicApiKey, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = source.Host, Port = source.Port, Database = source.Database, Username = source.Username,
            Password = source.Password, SslMode = Enum.Parse<SslMode>(source.SslMode, ignoreCase: true), Timeout = 30
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM anon, authenticated, service_role;
            ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public REVOKE USAGE, SELECT ON SEQUENCES FROM anon, authenticated, service_role;
            ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC, anon, authenticated, service_role;
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM anon, authenticated, service_role;
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM anon, authenticated, service_role;
            REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC, anon, authenticated, service_role;
            UPDATE public.auth_sessions SET revoked_at = COALESCE(revoked_at, now()) WHERE revoked_at IS NULL;
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction)) await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        const string verifySql = """
            WITH roles(role_name) AS (VALUES ('anon'),('authenticated'),('service_role')),
            remaining AS (
                SELECT grants.grantee AS role_name, grants.table_name AS object_name
                FROM information_schema.role_table_grants grants
                WHERE grants.table_schema='public' AND grants.grantee IN ('anon','authenticated','service_role')
                UNION ALL
                SELECT roles.role_name,c.relname
                FROM roles CROSS JOIN pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='public' AND c.relkind='S'
                  AND (has_sequence_privilege(roles.role_name,c.oid,'USAGE') OR has_sequence_privilege(roles.role_name,c.oid,'SELECT'))
                UNION ALL
                SELECT roles.role_name,p.proname
                FROM roles CROSS JOIN pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='public' AND has_function_privilege(roles.role_name,p.oid,'EXECUTE')
            )
            SELECT COUNT(*) FROM remaining
            """;
        await using var verify = new NpgsqlCommand(verifySql, connection);
        long grants = Convert.ToInt64(await verify.ExecuteScalarAsync(cancellationToken));
        if (grants != 0) throw new InvalidOperationException($"A proteção do Data API terminou com {grants} privilégios restantes em tabelas, sequências ou funções.");
        await VerifyPublicApiDeniedAsync(source, publicApiKey, cancellationToken);
    }

    private static async Task VerifyPublicApiDeniedAsync(MigrationSource source, string publicApiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicApiKey))
            throw new InvalidOperationException("Informe a chave pública anon para validar o bloqueio do Data API.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string key = publicApiKey.Trim();

        // First prove that the supplied key belongs to the project. Otherwise
        // an invalid key could make the denial test pass for the wrong reason.
        using (var authRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(source.Project.BaseUri, "/auth/v1/settings")))
        {
            authRequest.Headers.TryAddWithoutValidation("apikey", key);
            using HttpResponseMessage authResponse = await client.SendAsync(authRequest, cancellationToken);
            if (!authResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"A chave pública não foi aceita pelo projeto Supabase (HTTP {(int)authResponse.StatusCode}).");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(source.Project.BaseUri, "/rest/v1/stores?select=id&limit=1"));
        request.Headers.TryAddWithoutValidation("apikey", key);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        bool denied = response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.NotAcceptable
            || body.Contains("42501", StringComparison.OrdinalIgnoreCase)
            || body.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || body.Contains("PGRST106", StringComparison.OrdinalIgnoreCase);
        if (!denied)
            throw new InvalidOperationException($"A tabela stores ainda respondeu pelo Data API (HTTP {(int)response.StatusCode}). Remova public dos schemas expostos ou desative o Data API.");
    }

    internal async Task RunAsync(MigrationRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (request.FinalCutover && !request.MaintenanceConfirmed)
            throw new InvalidOperationException("Confirme que o backend cloud está em manutenção antes do corte.");
        if (request.Stores.Count == 0) throw new InvalidOperationException("Selecione pelo menos uma loja.");
        Directory.CreateDirectory(request.OutputDirectory);
        string runRoot = Path.Combine(Path.GetTempPath(), $"farmaflow-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);
        var journal = new MigrationRunJournal(Path.Combine(request.OutputDirectory, "migration-run.json"));
        var journalResults = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task JournalAsync(string step, string result)
        {
            journalResults[step] = result;
            await journal.SaveAsync(new MigrationRunState(
                Path.GetFileName(runRoot), request.FinalCutover ? "FINAL" : "REHEARSAL", step,
                DateTimeOffset.UtcNow, journalResults), cancellationToken);
        }
        string sourcePackage = Path.Combine(runRoot, "integral-v1.ffbackup");
        try
        {
            await JournalAsync("started", "Execução iniciada");
            progress.Report(new OperationProgress("snapshot", "Criando o arquivo integral do Supabase…", 10));
            ProcessResult exported = await RunMigrationAsync("export-full", request.Source.Password, request.PackagePassword, request.PackagePassword,
                ["--host", request.Source.Host, "--port", request.Source.Port.ToString(), "--database", request.Source.Database,
                 "--username", request.Source.Username, "--pg-bin", request.PostgresBin, "--ssl-mode", request.Source.SslMode, "--output", sourcePackage], cancellationToken);
            EnsureSuccess(exported, "Não foi possível criar o snapshot integral.");
            await ConvertLegacyPackageAsync(sourcePackage, Path.Combine(request.OutputDirectory, request.FinalCutover ? "farmaflow-integral.ffarchive" : "farmaflow-integral-ensaio.ffarchive"), request.PackagePassword, cancellationToken);
            if (request.FinalCutover && request.DataApiConfirmed)
            {
                progress.Report(new OperationProgress("security", "Aplicando os REVOKE do Data API após o backup…", 18));
                await HardenDataApiAsync(request.Source, request.PublicApiKey, cancellationToken);
                await JournalAsync("security", "Privilégios do Data API revogados e verificados");
            }

            await using var cluster = await LocalPostgresCluster.StartAsync(request.PostgresBin, Path.Combine(runRoot, "postgres"), cancellationToken);
            string stagingPassword = cluster.Password;
            for (int index = 0; index < request.Stores.Count; index++)
            {
                StoreChoice store = request.Stores[index];
                string staging = $"farmaflow_staging_{index + 1}";
                int basePercent = 20 + (index * 70 / request.Stores.Count);
                progress.Report(new OperationProgress("restore", $"Preparando {store.Name}…", basePercent));
                ProcessResult created = await ProcessRunner.RunAsync(Path.Combine(request.PostgresBin, "createdb.exe"),
                    ["--host", "127.0.0.1", "--port", cluster.Port.ToString(), "--username", "farmaflow", staging],
                    environment: new Dictionary<string, string> { ["PGPASSWORD"] = stagingPassword }, cancellationToken: cancellationToken);
                EnsureSuccess(created, $"Não foi possível criar o staging de {store.Name}.");
                ProcessResult restored = await RunMigrationAsync("restore", request.PackagePassword, stagingPassword,
                    null, ["--input", sourcePackage, "--host", "127.0.0.1", "--port", cluster.Port.ToString(), "--database", staging,
                    "--username", "farmaflow", "--pg-bin", request.PostgresBin], cancellationToken);
                EnsureSuccess(restored, $"Não foi possível restaurar o staging de {store.Name}.");
                ProcessResult filtered = await RunMigrationAsync("filter-store-staging", stagingPassword, null, staging,
                    ["--host", "127.0.0.1", "--port", cluster.Port.ToString(), "--database", staging, "--username", "farmaflow", "--store-id", store.Id.ToString()], cancellationToken);
                EnsureSuccess(filtered, $"Não foi possível isolar {store.Name}.");
                progress.Report(new OperationProgress("media", $"Arquivando mídias de {store.Name}…", basePercent + 10));
                ProcessResult media = await RunMigrationAsync("archive-media", stagingPassword, null, null,
                    ["--host", "127.0.0.1", "--port", cluster.Port.ToString(), "--database", staging, "--username", "farmaflow"], cancellationToken);
                EnsureSuccess(media, $"Não foi possível arquivar as mídias de {store.Name}.");
                string storeV1 = Path.Combine(runRoot, $"store-{index + 1}-v1.ffbackup");
                ProcessResult storeExport = await RunMigrationAsync("export-full", stagingPassword, request.PackagePassword, request.PackagePassword,
                    ["--host", "127.0.0.1", "--port", cluster.Port.ToString(), "--database", staging, "--username", "farmaflow", "--pg-bin", request.PostgresBin,
                     "--ssl-mode", "Prefer", "--store-id", store.Id.ToString(), "--output", storeV1], cancellationToken);
                EnsureSuccess(storeExport, $"Não foi possível criar o pacote de {store.Name}.");
                string storeFileName = $"{Sanitize(store.Name)}-{store.Id:N}.ffstore";
                await ConvertLegacyPackageAsync(storeV1, Path.Combine(request.OutputDirectory, storeFileName), request.PackagePassword, cancellationToken);
                await ProcessRunner.RunAsync(Path.Combine(request.PostgresBin, "dropdb.exe"),
                    ["--host", "127.0.0.1", "--port", cluster.Port.ToString(), "--username", "farmaflow", staging],
                    environment: new Dictionary<string, string> { ["PGPASSWORD"] = stagingPassword }, cancellationToken: cancellationToken);
                progress.Report(new OperationProgress("store", $"{store.Name} validada.", basePercent + 25));
                await JournalAsync($"store-{store.Id:N}", "Pacote gerado e validado");
            }
            progress.Report(new OperationProgress("complete", "Migração concluída e validada.", 100));
            await WriteReportsAsync(request, cancellationToken);
            await JournalAsync("complete", "Pacotes gerados e staging removido");
        }
        catch
        {
            try { await JournalAsync("failed", "Execução interrompida; nenhum segredo foi registrado"); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(runRoot, recursive: true); } catch { }
        }
    }

    private async Task<ProcessResult> RunMigrationAsync(string command, string? firstSecret, string? secondSecret, string? confirmation,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var secrets = new List<string>();
        if (firstSecret is not null) secrets.Add(firstSecret);
        if (secondSecret is not null) secrets.Add(secondSecret);
        if (confirmation is not null) secrets.Add(confirmation);
        return await ProcessRunner.RunAsync(_migrationExecutable, [command, .. arguments], secrets, cancellationToken: cancellationToken);
    }

    private static async Task ConvertLegacyPackageAsync(string sourcePath, string destinationPath, string password, CancellationToken cancellationToken)
    {
        string manifestPath = $"{sourcePath}.json";
        if (!File.Exists(manifestPath)) throw new InvalidDataException("O pacote legado não contém manifesto.");
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        PackageEnvelope.PackageReadResult package = await PackageEnvelope.ReadAsync(sourcePath, password, cancellationToken);
        JsonObject normalized = JsonNode.Parse(manifest.RootElement.GetRawText())!.AsObject();
        normalized.Remove("packageSha256");
        normalized["formatVersion"] = 2;
        using JsonDocument normalizedManifest = JsonDocument.Parse(normalized.ToJsonString());
        await PackageEnvelope.WriteV2Async(destinationPath, package.Plaintext, normalizedManifest.RootElement, password, cancellationToken);
        CryptographicOperations.ZeroMemory(package.Plaintext);
    }

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"{message} {Redact(result.Error)}");
    }

    private static string Sanitize(string value)
    {
        string result = string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        return string.IsNullOrWhiteSpace(result) ? "loja" : result.Trim('-');
    }

    private static string Trim(string value) => value.Length <= 400 ? value : value[..400];
    private static string Redact(string value)
    {
        string redacted = Regex.Replace(value, "(?i)(password|passwd|pwd|user|username|apikey|token)=\\S+", "$1=[redacted]");
        redacted = Regex.Replace(redacted, "(?i)(postgres(?:ql)?://)[^/@]+@", "$1[redacted]@");
        return Trim(redacted);
    }

    private static async Task WriteReportsAsync(MigrationRequest request, CancellationToken cancellationToken)
    {
        var files = new List<MigrationReportFile>();
        foreach (string path in Directory.EnumerateFiles(request.OutputDirectory, "*.ffarchive")
            .Concat(Directory.EnumerateFiles(request.OutputDirectory, "*.ffstore")))
        {
            files.Add(new MigrationReportFile(Path.GetFileName(path), new FileInfo(path).Length,
                await PackageEnvelope.Sha256FileAsync(path, cancellationToken)));
        }
        var report = new
        {
            format = "FarmaFlow migration report",
            generatedAt = DateTimeOffset.UtcNow,
            mode = request.FinalCutover ? "FINAL" : "REHEARSAL",
            stores = request.Stores.Select(store => new { id = store.Id, name = store.Name, organizationId = store.OrganizationId }),
            files
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(request.OutputDirectory, "farmaflow-migration-report.json"), json, cancellationToken);
        string rows = string.Join(Environment.NewLine, files.Select(file =>
            $"<li>{WebUtility.HtmlEncode(file.Name)} — {file.Bytes:N0} bytes — SHA-256 {file.Sha256}</li>"));
        string html = $"<!doctype html><meta charset=\"utf-8\"><title>FarmaFlow — relatório de migração</title><h1>Relatório de migração FarmaFlow</h1><p>Modo: {WebUtility.HtmlEncode(request.FinalCutover ? "corte definitivo" : "ensaio")}</p><p>Gerado em: {report.generatedAt:O}</p><h2>Lojas</h2><ul>{string.Join("", request.Stores.Select(store => $"<li>{WebUtility.HtmlEncode(store.Name)} ({store.Id:N})</li>"))}</ul><h2>Arquivos</h2><ul>{rows}</ul>";
        await File.WriteAllTextAsync(Path.Combine(request.OutputDirectory, "farmaflow-migration-report.html"), html, cancellationToken);
    }
}
