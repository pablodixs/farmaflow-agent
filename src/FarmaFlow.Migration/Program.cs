using Npgsql;
using FarmaFlow.Migration.Core;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const int minimumSchemaVersion = 52;
const int maximumSchemaVersion = 54;

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("""
        FarmaFlow.Migration export-full --host HOST --port 5432 --database postgres --username USER --pg-bin DIR --output arquivo.ffbackup [--store-id UUID] [--ssl-mode Require|Prefer|Disable]
        FarmaFlow.Migration verify --input arquivo.ffbackup
        FarmaFlow.Migration convert-package --input arquivo.ffbackup --output pacote.ffstore
        FarmaFlow.Migration restore --input arquivo.ffbackup --host 127.0.0.1 --port 54329 --database farmaflow --username farmaflow --pg-bin DIR
        FarmaFlow.Migration restore-server-backup --input backup.ffbackup --host 127.0.0.1 --port 54329 --database farmaflow --username farmaflow --pg-bin DIR
        FarmaFlow.Migration filter-store-staging --host 127.0.0.1 --port 54329 --database farmaflow_staging --username farmaflow --store-id UUID
        FarmaFlow.Migration archive-media --host 127.0.0.1 --port 54329 --database farmaflow_staging --username farmaflow

        As senhas da origem e do pacote são solicitadas de forma oculta e nunca são persistidas.
        export-full cria um snapshot consistente, somente leitura, do schema public.
        """);
    return;
}

var arguments = ParseArguments(args.Skip(1));
switch (args[0].ToLowerInvariant())
{
    case "export-full":
        await ExportFullAsync(arguments);
        break;
    case "verify":
        await VerifyAsync(Required(arguments, "input"));
        break;
    case "convert-package":
    case "convert-v2": // alias mantido para automações antigas
        await ConvertV2Async(arguments);
        break;
    case "restore":
        await RestoreAsync(arguments);
        break;
    case "restore-server-backup":
        await RestoreServerBackupAsync(arguments);
        break;
    case "filter-store-staging":
        await FarmaFlow.Migration.StoreFilter.RunAsync(arguments);
        break;
    case "archive-media":
        await FarmaFlow.Migration.MediaArchiver.RunAsync(arguments);
        break;
    default:
        throw new InvalidOperationException($"Comando desconhecido: {args[0]}");
}

async Task ExportFullAsync(IReadOnlyDictionary<string, string> values)
{
    string host = Required(values, "host");
    int port = int.Parse(values.GetValueOrDefault("port", "5432"));
    string database = values.GetValueOrDefault("database", "postgres");
    string username = Required(values, "username");
    string pgBin = Required(values, "pg-bin");
    string output = Path.GetFullPath(Required(values, "output"));
    Guid? packageStoreId = values.TryGetValue("store-id", out string? storeValue) ? Guid.Parse(storeValue) : null;
    SslMode sslMode = ResolveSslMode(values, host);
    string sourcePassword = ReadSecret("Senha do PostgreSQL de origem: ");
    string packagePassword = ReadSecret("Senha do pacote criptografado: ");
    string confirmation = ReadSecret("Confirme a senha do pacote: ");
    byte[] packagePasswordBytes = Encoding.UTF8.GetBytes(packagePassword);
    byte[] confirmationBytes = Encoding.UTF8.GetBytes(confirmation);
    try
    {
        if (!CryptographicOperations.FixedTimeEquals(packagePasswordBytes, confirmationBytes))
            throw new InvalidOperationException("As senhas do pacote não coincidem.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(packagePasswordBytes);
        CryptographicOperations.ZeroMemory(confirmationBytes);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    string temporary = CreateTemporaryFilePath("export");
    var connectionBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = port,
        Database = database,
        Username = username,
        Password = sourcePassword,
        SslMode = sslMode,
        Timeout = 30,
        CommandTimeout = 0
    };

    try
    {
        await using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync();
        string snapshot = (string)(await new NpgsqlCommand("SELECT pg_export_snapshot()", connection, transaction).ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Não foi possível exportar o snapshot PostgreSQL."));

        string schemaVersion = await ScalarAsync(connection, transaction,
            "SELECT COALESCE((SELECT version FROM public.flyway_schema_history WHERE success ORDER BY installed_rank DESC LIMIT 1), '0')") ?? "0";
        Guid? packageOrganizationId = null;
        if (packageStoreId is not null)
        {
            await using var identity = new NpgsqlCommand("SELECT id,organization_id FROM public.stores", connection, transaction);
            await using var reader = await identity.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("Um pacote de loja exige um staging contendo exatamente a loja informada.");
            Guid actualStoreId = reader.GetGuid(0);
            packageOrganizationId = reader.GetGuid(1);
            if (actualStoreId != packageStoreId || await reader.ReadAsync())
                throw new InvalidOperationException("Um pacote de loja exige um staging contendo exatamente a loja informada.");
        }
        await ValidateSchemaHistoryAsync(connection, transaction, schemaVersion);
        var counts = await ReadCountsAsync(connection, transaction);
        var reconciliation = await ReadReconciliationAsync(connection, transaction);
        var extensions = await ReadExtensionsAsync(connection, transaction);
        string sourceServerVersion = await ScalarAsync(connection, transaction, "SHOW server_version") ?? "unknown";
        await RunPgDumpAsync(pgBin, host, port, database, username, sourcePassword, sslMode, snapshot, temporary);
        string restoreCatalog = await RunPgRestoreListAsync(pgBin, temporary);
        await transaction.RollbackAsync();

        string plaintextSha256 = await Sha256FileAsync(temporary);

        var manifest = new
        {
            format = "FarmaFlow migration backup",
            formatVersion = 3,
            kind = packageStoreId is null ? "FULL_ARCHIVE" : "STORE",
            storeId = packageStoreId,
            organizationId = packageOrganizationId,
            schema = "public",
            schemaVersion,
            sourceDatabaseVersion = sourceServerVersion,
            targetDatabaseMajorVersion = 17,
            databaseMajorVersion = 17,
            createdAt = DateTimeOffset.UtcNow,
            source = new { host, port, database },
            extensions,
            tables = counts,
            reconciliation,
            plaintextSha256,
            pgRestoreCatalogSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(restoreCatalog)))
        };
        using JsonDocument embeddedManifest = JsonDocument.Parse(JsonSerializer.Serialize(manifest));
        string packageSha256 = await PackageEnvelope.WriteV3Async(output, temporary, embeddedManifest.RootElement, packagePassword);
        JsonObject sidecar = JsonNode.Parse(embeddedManifest.RootElement.GetRawText())!.AsObject();
        sidecar["packageSha256"] = packageSha256;
        await WriteTextAtomicallyAsync($"{output}.json", sidecar.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Snapshot consistente criado: {output}");
        Console.WriteLine($"Manifesto: {output}.json");
    }
    finally
    {
        sourcePassword = string.Empty;
        packagePassword = string.Empty;
        confirmation = string.Empty;
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}

async Task VerifyAsync(string input)
{
    input = Path.GetFullPath(input);
    string password = ReadSecret("Senha do pacote: ");
    try
    {
        PackageEnvelope.PackageExtractResult package = await PackageEnvelope.ExtractAsync(input, null, password);
        string kind = "LEGADO";
        if (package.Manifest is not null && package.Manifest.RootElement.TryGetProperty("kind", out JsonElement value))
            kind = value.GetString() ?? "PACOTE";
        Console.WriteLine($"Pacote íntegro ({kind}). Dump autenticado: {package.PlaintextLength:N0} bytes; SHA-256 {package.PlaintextSha256}");
        package.Manifest?.Dispose();
    }
    finally
    {
        password = string.Empty;
    }
}

async Task ConvertV2Async(IReadOnlyDictionary<string, string> values)
{
    string input = Path.GetFullPath(Required(values, "input"));
    string output = Path.GetFullPath(Required(values, "output"));
    string password = ReadSecret("Senha do pacote: ");
    string temporary = CreateTemporaryFilePath("convert");
    PackageEnvelope.PackageExtractResult? package = null;
    try
    {
        string manifestPath = $"{input}.json";
        if (!File.Exists(manifestPath)) throw new InvalidDataException("O pacote legado não contém manifesto.");
        using JsonDocument sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        package = await PackageEnvelope.ExtractAsync(input, temporary, password);
        JsonElement authoritative = sidecar.RootElement;
        if (package.Manifest is not null)
        {
            if (!SidecarMatchesEmbedded(sidecar.RootElement, package.Manifest.RootElement))
                throw new InvalidDataException("O manifesto lateral diverge do manifesto autenticado dentro do pacote legado.");
            authoritative = package.Manifest.RootElement;
        }
        JsonObject normalized = JsonNode.Parse(authoritative.GetRawText())!.AsObject();
        normalized.Remove("packageSha256");
        normalized["formatVersion"] = 3;
        using JsonDocument normalizedManifest = JsonDocument.Parse(normalized.ToJsonString());
        await PackageEnvelope.WriteV3Async(output, temporary, normalizedManifest.RootElement, password);
        Console.WriteLine($"Pacote streaming v3 criado: {output}");
    }
    finally
    {
        package?.Manifest?.Dispose();
        if (File.Exists(temporary)) File.Delete(temporary);
        password = string.Empty;
    }
}

async Task RestoreAsync(IReadOnlyDictionary<string, string> values)
{
    string input = Path.GetFullPath(Required(values, "input"));
    string host = values.GetValueOrDefault("host", "127.0.0.1");
    int port = int.Parse(values.GetValueOrDefault("port", "54329"));
    string database = values.GetValueOrDefault("database", "farmaflow");
    string username = values.GetValueOrDefault("username", "farmaflow");
    string pgBin = Required(values, "pg-bin");
    string packagePassword = ReadSecret("Senha do pacote: ");
    string targetPassword = ReadSecret("Senha do PostgreSQL local: ");
    string temporary = CreateTemporaryFilePath("restore");
    PackageEnvelope.PackageExtractResult? packageEnvelope = null;
    JsonDocument? expectedManifest = null;
    JsonDocument? sidecarManifest = null;
    try
    {
        string manifestPath = $"{input}.json";
        if (File.Exists(manifestPath))
        {
            sidecarManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            string expectedPackageHash = sidecarManifest.RootElement.GetProperty("packageSha256").GetString() ?? string.Empty;
            if (!string.Equals(expectedPackageHash, await Sha256FileAsync(input), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O checksum do pacote não corresponde ao manifesto.");
        }

        packageEnvelope = await PackageEnvelope.ExtractAsync(input, temporary, packagePassword);
        // O manifesto incorporado é autenticado e sempre prevalece. O sidecar
        // serve apenas para conferência humana e compatibilidade com o v1.
        if (packageEnvelope.Manifest is not null)
        {
            if (sidecarManifest is not null && !SidecarMatchesEmbedded(sidecarManifest.RootElement, packageEnvelope.Manifest.RootElement))
                throw new InvalidDataException("O manifesto lateral diverge do manifesto autenticado dentro do pacote.");
            expectedManifest = packageEnvelope.Manifest;
        }
        else
        {
            expectedManifest = sidecarManifest;
        }
        string catalog = await RunPgRestoreListAsync(pgBin, temporary);
        if (expectedManifest is not null && expectedManifest.RootElement.TryGetProperty("pgRestoreCatalogSha256", out JsonElement catalogHash))
        {
            string expectedCatalogHash = catalogHash.GetString() ?? string.Empty;
            string actualCatalogHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog)));
            if (!string.Equals(expectedCatalogHash, actualCatalogHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O catálogo pg_restore não corresponde ao manifesto.");
        }
        await ValidateTargetPostgresAsync(host, port, database, username, targetPassword, expectedManifest);
        await EnsureRequiredExtensionsAsync(host, port, database, username, targetPassword, expectedManifest, catalog);
        await RunPgRestoreAsync(pgBin, host, port, database, username, targetPassword, temporary);

        var connectionBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = targetPassword,
            SslMode = SslMode.Prefer,
            Timeout = 30,
            CommandTimeout = 0
        };
        await using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var revoke = new NpgsqlCommand("UPDATE public.auth_sessions SET revoked_at = now() WHERE revoked_at IS NULL", connection, transaction))
            Console.WriteLine($"Sessões invalidadas: {await revoke.ExecuteNonQueryAsync()}");
        string version = await ScalarAsync(connection, transaction,
            "SELECT COALESCE((SELECT version FROM public.flyway_schema_history WHERE success ORDER BY installed_rank DESC LIMIT 1), '0')") ?? "0";
        ValidateSupportedSchemaVersion(version, "restaurado");
        var actualCounts = await ReadCountsAsync(connection, transaction);
        var actualReconciliation = await ReadReconciliationAsync(connection, transaction);
        if (expectedManifest is not null)
        {
            string expectedCounts = expectedManifest.RootElement.GetProperty("tables").GetRawText();
            string actualCountsJson = JsonSerializer.Serialize(actualCounts);
            if (!JsonEquivalent(expectedCounts, actualCountsJson))
                throw new InvalidOperationException("As contagens restauradas divergem do manifesto de origem.");
            string expectedReconciliation = expectedManifest.RootElement.GetProperty("reconciliation").GetRawText();
            string actualReconciliationJson = JsonSerializer.Serialize(actualReconciliation);
            if (!ReconciliationEquivalent(expectedReconciliation, actualReconciliationJson))
                throw new InvalidOperationException("Os totais financeiros, de estoque, caixa ou sequências divergem da origem.");
        }
        await ValidateSequencesAsync(connection, transaction);
        await transaction.CommitAsync();
        Console.WriteLine($"Restauração concluída e validada no schema V{version}.");
    }
    finally
    {
        packagePassword = string.Empty;
        targetPassword = string.Empty;
        packageEnvelope?.Manifest?.Dispose();
        if (expectedManifest is not null && !ReferenceEquals(expectedManifest, packageEnvelope?.Manifest) && !ReferenceEquals(expectedManifest, sidecarManifest)) expectedManifest.Dispose();
        sidecarManifest?.Dispose();
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}

async Task RestoreServerBackupAsync(IReadOnlyDictionary<string, string> values)
{
    string input = Path.GetFullPath(Required(values, "input"));
    string host = values.GetValueOrDefault("host", "127.0.0.1");
    int port = int.Parse(values.GetValueOrDefault("port", "54329"));
    string database = values.GetValueOrDefault("database", "farmaflow");
    string username = values.GetValueOrDefault("username", "farmaflow");
    string pgBin = Required(values, "pg-bin");
    string recoveryKey = ReadSecret("Chave de recuperação Base64: ");
    string targetPassword = ReadSecret("Senha do PostgreSQL local: ");
    string temporary = CreateTemporaryFilePath("server-restore");
    byte[] serverMagic = "FFBACKUP"u8.ToArray();
    PackageEnvelope.PackageExtractResult? package = null;
    try
    {
        byte[] prefix = new byte[serverMagic.Length];
        await using (FileStream stream = File.OpenRead(input))
        {
            int read = 0;
            while (read < prefix.Length)
            {
                int current = await stream.ReadAsync(prefix.AsMemory(read));
                if (current == 0) break;
                read += current;
            }
            if (read < 6) throw new InvalidDataException("O arquivo não é um backup diário FarmaFlow válido.");
        }
        if (prefix.AsSpan().StartsWith("FFMIG3"u8))
        {
            package = await PackageEnvelope.ExtractAsync(input, temporary, recoveryKey);
            if (package.Manifest is null
                || !package.Manifest.RootElement.TryGetProperty("kind", out JsonElement kind)
                || !string.Equals(kind.GetString(), "SERVER_BACKUP", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O arquivo é um pacote FarmaFlow, mas não é um backup diário do servidor.");
        }
        else
        {
            // Compatibilidade com backups diários v1, que usavam AES-GCM em
            // uma única mensagem e, por definição, não podem ser lidos em fluxo.
            byte[] payload = await File.ReadAllBytesAsync(input);
            byte[] plaintext = [];
            byte[] key = [];
            try
            {
                if (payload.Length < serverMagic.Length + sizeof(int) + 12 + 16 || !payload.AsSpan(0, serverMagic.Length).SequenceEqual(serverMagic))
                    throw new InvalidDataException("O arquivo não é um backup diário FarmaFlow válido.");
                int version = BitConverter.ToInt32(payload, serverMagic.Length);
                if (version != 1) throw new InvalidDataException($"Versão de backup não suportada: {version}");
                int offset = serverMagic.Length + sizeof(int);
                byte[] nonce = payload.AsSpan(offset, 12).ToArray();
                byte[] tag = payload.AsSpan(offset + 12, 16).ToArray();
                byte[] ciphertext = payload.AsSpan(offset + 28).ToArray();
                plaintext = new byte[ciphertext.Length];
                key = Convert.FromBase64String(recoveryKey);
                try
                {
                    using var aes = new AesGcm(key, tag.Length);
                    aes.Decrypt(nonce, ciphertext, tag, plaintext, serverMagic);
                }
                catch (CryptographicException exception)
                {
                    throw new InvalidDataException("Chave incorreta ou backup diário adulterado.", exception);
                }
                await File.WriteAllBytesAsync(temporary, plaintext);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(ciphertext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        string catalog = await RunPgRestoreListAsync(pgBin, temporary);
        if (package?.Manifest is not null && package.Manifest.RootElement.TryGetProperty("pgRestoreCatalogSha256", out JsonElement expectedCatalog))
        {
            string actualCatalog = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog)));
            if (!string.Equals(expectedCatalog.GetString(), actualCatalog, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O catálogo do backup diário não corresponde ao manifesto autenticado.");
        }
        await RunPgRestoreAsync(pgBin, host, port, database, username, targetPassword, temporary);

        var connectionBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = database, Username = username,
            Password = targetPassword, SslMode = SslMode.Prefer, Timeout = 30, CommandTimeout = 0
        };
        await using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
        await connection.OpenAsync();
        string schemaVersion = await ScalarAsync(connection, null,
            "SELECT COALESCE((SELECT version FROM public.flyway_schema_history WHERE success ORDER BY installed_rank DESC LIMIT 1), '0')") ?? "0";
        ValidateSupportedSchemaVersion(schemaVersion, "do backup");
        long failed = Convert.ToInt64(await new NpgsqlCommand("SELECT COUNT(*) FROM public.flyway_schema_history WHERE NOT success", connection).ExecuteScalarAsync());
        if (failed != 0) throw new InvalidOperationException($"O backup restaurado contém {failed} migration(s) Flyway com falha.");
        Console.WriteLine($"Backup diário restaurado e validado no schema V{schemaVersion}.");
    }
    finally
    {
        recoveryKey = string.Empty;
        targetPassword = string.Empty;
        package?.Manifest?.Dispose();
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}

static async Task RunPgDumpAsync(string pgBin, string host, int port, string database, string username, string password, SslMode sslMode, string snapshot, string output)
{
    string executable = Path.Combine(pgBin, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
    if (!File.Exists(executable)) throw new FileNotFoundException("pg_dump não encontrado.", executable);
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (string argument in new[]
    {
        $"--host={host}", $"--port={port}", $"--username={username}", $"--dbname={database}",
        "--format=custom", "--no-owner", "--no-acl", "--schema=public", $"--snapshot={snapshot}", $"--file={output}"
    }) startInfo.ArgumentList.Add(argument);
    startInfo.Environment["PGPASSWORD"] = password;
    startInfo.Environment["PGSSLMODE"] = sslMode switch
    {
        SslMode.Disable => "disable",
        SslMode.Allow => "allow",
        SslMode.Prefer => "prefer",
        SslMode.Require => "require",
        SslMode.VerifyCA => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => throw new InvalidOperationException($"Modo SSL não suportado pelo pg_dump: {sslMode}.")
    };
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar pg_dump.");
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    string error = await errorTask;
    if (process.ExitCode != 0) throw new InvalidOperationException($"pg_dump falhou: {error}");
}

static async Task<string> RunPgRestoreListAsync(string pgBin, string input)
{
    string executable = Path.Combine(pgBin, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore");
    if (!File.Exists(executable)) throw new FileNotFoundException("pg_restore não encontrado.", executable);
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("--list");
    startInfo.ArgumentList.Add(input);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar pg_restore.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    string output = await outputTask;
    string error = await errorTask;
    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        throw new InvalidOperationException($"A validação do catálogo pg_restore falhou: {error}");
    return output;
}

static async Task RunPgRestoreAsync(string pgBin, string host, int port, string database, string username, string password, string input)
{
    string executable = Path.Combine(pgBin, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore");
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (string argument in new[]
    {
        $"--host={host}", $"--port={port}", $"--username={username}", $"--dbname={database}",
        "--exit-on-error", "--single-transaction", "--clean", "--if-exists", "--no-owner", "--no-acl", input
    }) startInfo.ArgumentList.Add(argument);
    startInfo.Environment["PGPASSWORD"] = password;
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar pg_restore.");
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    string error = await errorTask;
    if (process.ExitCode != 0) throw new InvalidOperationException($"pg_restore falhou: {error}");
}

static async Task<SortedDictionary<string, long>> ReadCountsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    var tables = new List<string>();
    const string query = "SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname='public' ORDER BY tablename";
    await using (var command = new NpgsqlCommand(query, connection, transaction))
    await using (var reader = await command.ExecuteReaderAsync())
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

    var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
    foreach (string table in tables)
    {
        string quoted = $"\"{table.Replace("\"", "\"\"")}\"";
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{quoted}", connection, transaction);
        result[table] = Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    return result;
}

static async Task<SortedDictionary<string, JsonElement>> ReadReconciliationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    var queries = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["salesByDay"] = "SELECT created_at::date AS day, store_id, COUNT(*) AS records, COALESCE(SUM(total_amount),0) AS total FROM public.sales GROUP BY 1,2 ORDER BY 1,2",
        ["paymentsByDay"] = "SELECT s.created_at::date AS day, s.store_id, COUNT(*) AS records, COALESCE(SUM(p.amount),0) AS total FROM public.sale_payments p JOIN public.sales s ON s.id=p.sale_id GROUP BY 1,2 ORDER BY 1,2",
        ["inventory"] = "SELECT store_id, product_id, quantity, reserved_quantity FROM public.store_inventories ORDER BY store_id, product_id",
        ["lots"] = "SELECT store_id, product_id, lot_number, available_quantity, reserved_quantity FROM public.inventory_lots ORDER BY store_id, product_id, lot_number, id",
        ["cashByDay"] = "SELECT created_at::date AS day, store_id, type, COUNT(*) AS records, COALESCE(SUM(amount),0) AS total FROM public.cash_movements GROUP BY 1,2,3 ORDER BY 1,2,3",
        ["purchases"] = "SELECT store_id, COUNT(*) AS records, COALESCE(SUM(total_invoice),0) AS total FROM public.purchase_invoices GROUP BY store_id ORDER BY store_id",
        ["stocktakes"] = "SELECT store_id, status, COUNT(*) AS records FROM public.stocktakes GROUP BY store_id,status ORDER BY store_id,status"
    };
    var result = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
    bool hasLocalMedia = !string.IsNullOrEmpty(await ScalarAsync(
        connection,
        transaction,
        "SELECT to_regclass('public.local_media_blobs')::text"));
    if (hasLocalMedia)
        queries["media"] = "SELECT media_id,missing,sha256,source_url,failure FROM public.local_media_blobs ORDER BY media_id";
    foreach ((string name, string query) in queries)
    {
        string sql = $"SELECT COALESCE(jsonb_agg(row_to_json(record)), '[]'::jsonb)::text FROM ({query}) record";
        string json = await ScalarAsync(connection, transaction, sql) ?? "[]";
        using JsonDocument document = JsonDocument.Parse(json);
        result[name] = document.RootElement.Clone();
    }
    return result;
}

static bool JsonEquivalent(string left, string right)
{
    using JsonDocument leftDocument = JsonDocument.Parse(left);
    using JsonDocument rightDocument = JsonDocument.Parse(right);
    return JsonElementsEquivalent(leftDocument.RootElement, rightDocument.RootElement);
}

static bool ReconciliationEquivalent(string left, string right)
{
    JsonObject leftObject = JsonNode.Parse(left)!.AsObject();
    JsonObject rightObject = JsonNode.Parse(right)!.AsObject();
    // Legacy manifests recorded volatile sequence last_value values. Sequences
    // are validated structurally after restore instead of requiring exact equality.
    leftObject.Remove("sequences");
    rightObject.Remove("sequences");
    return JsonEquivalent(leftObject.ToJsonString(), rightObject.ToJsonString());
}

async Task ValidateSchemaHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string version)
{
    await using var command = new NpgsqlCommand(
        "SELECT COUNT(*) FROM public.flyway_schema_history WHERE NOT success", connection, transaction);
    long failed = Convert.ToInt64(await command.ExecuteScalarAsync());
    if (failed != 0)
        throw new InvalidOperationException($"O Flyway contém {failed} migration(s) com falha. Execute o repair e valide o schema antes de exportar.");
    ValidateSupportedSchemaVersion(version, "de origem");
}

void ValidateSupportedSchemaVersion(string version, string context)
{
    if (!int.TryParse(version, out int numericVersion))
        throw new InvalidOperationException($"A versão Flyway {version} {context} não é numérica nem suportada.");
    if (numericVersion < minimumSchemaVersion || numericVersion > maximumSchemaVersion)
        throw new InvalidOperationException(
            $"Schema {context} V{numericVersion} incompatível com esta release. Versões suportadas: V{minimumSchemaVersion} a V{maximumSchemaVersion}.");
}

static async Task<IReadOnlyList<object>> ReadExtensionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    const string sql = """
        SELECT extension.extname, namespace.nspname, extension.extversion
        FROM pg_extension extension
        JOIN pg_namespace namespace ON namespace.oid=extension.extnamespace
        WHERE extension.extname <> 'plpgsql'
        ORDER BY extension.extname
        """;
    var result = new List<object>();
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        result.Add(new { name = reader.GetString(0), schema = reader.GetString(1), version = reader.GetString(2) });
    return result;
}

static async Task EnsureRequiredExtensionsAsync(
    string host,
    int port,
    string database,
    string username,
    string password,
    JsonDocument? manifest,
    string restoreCatalog)
{
    var extensions = new List<(string Name, string Schema)>();
    if (manifest is not null && manifest.RootElement.TryGetProperty("extensions", out JsonElement values))
    {
        foreach (JsonElement item in values.EnumerateArray())
            extensions.Add((item.GetProperty("name").GetString() ?? string.Empty, item.GetProperty("schema").GetString() ?? "public"));
    }
    else if (!restoreCatalog.Contains(" EXTENSION - pg_trgm ", StringComparison.OrdinalIgnoreCase))
    {
        // V18 and later require pg_trgm. Old package manifests did not inventory extensions.
        extensions.Add(("pg_trgm", "public"));
    }

    if (extensions.Count == 0) return;
    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = host, Port = port, Database = database, Username = username,
        Password = password, SslMode = SslMode.Prefer, Timeout = 30, CommandTimeout = 0
    };
    await using var connection = new NpgsqlConnection(builder.ConnectionString);
    await connection.OpenAsync();
    foreach ((string name, string schema) in extensions)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(schema))
            throw new InvalidDataException("O manifesto contém uma extensão PostgreSQL inválida.");
        if (restoreCatalog.Contains($" EXTENSION - {name} ", StringComparison.OrdinalIgnoreCase)) continue;
        string quotedName = QuoteIdentifier(name);
        string quotedSchema = QuoteIdentifier(schema);
        await using var command = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS {quotedSchema}; CREATE EXTENSION IF NOT EXISTS {quotedName} WITH SCHEMA {quotedSchema}", connection);
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"A extensão PostgreSQL '{name}' exigida pelo pacote não pôde ser instalada no schema '{schema}'. Confirme que o runtime contém a extensão e tente novamente.", exception);
        }
    }
}

static async Task ValidateTargetPostgresAsync(
    string host,
    int port,
    string database,
    string username,
    string password,
    JsonDocument? manifest)
{
    int expectedMajor = 17;
    if (manifest is not null)
    {
        JsonElement root = manifest.RootElement;
        if (root.TryGetProperty("targetDatabaseMajorVersion", out JsonElement target)) expectedMajor = target.GetInt32();
        else if (root.TryGetProperty("databaseMajorVersion", out JsonElement legacy)) expectedMajor = legacy.GetInt32();
    }
    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = host, Port = port, Database = database, Username = username,
        Password = password, SslMode = SslMode.Prefer, Timeout = 30
    };
    await using var connection = new NpgsqlConnection(builder.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
    string raw = Convert.ToString(await command.ExecuteScalarAsync()) ?? "0";
    if (!int.TryParse(raw, out int versionNumber) || versionNumber / 10_000 != expectedMajor)
        throw new InvalidOperationException($"O pacote requer PostgreSQL {expectedMajor}, mas o destino informou server_version_num={raw}. Use o runtime correto antes de restaurar.");
}

static async Task ValidateSequencesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    const string owned = """
        SELECT n.nspname,c.relname,a.attname,pg_get_serial_sequence(format('%I.%I',n.nspname,c.relname),a.attname)
        FROM pg_class c
        JOIN pg_namespace n ON n.oid=c.relnamespace
        JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
        WHERE n.nspname='public'
          AND pg_get_serial_sequence(format('%I.%I',n.nspname,c.relname),a.attname) IS NOT NULL
        """;
    var sequences = new List<(string Schema, string Table, string Column, string Sequence)>();
    await using (var command = new NpgsqlCommand(owned, connection, transaction))
    await using (var reader = await command.ExecuteReaderAsync())
        while (await reader.ReadAsync()) sequences.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

    foreach (var item in sequences)
    {
        string table = $"{QuoteIdentifier(item.Schema)}.{QuoteIdentifier(item.Table)}";
        string column = QuoteIdentifier(item.Column);
        long? maximum;
        await using (var max = new NpgsqlCommand($"SELECT MAX({column})::bigint FROM {table}", connection, transaction))
        {
            object? value = await max.ExecuteScalarAsync();
            maximum = value is null or DBNull ? null : Convert.ToInt64(value);
        }
        await using var state = new NpgsqlCommand($"SELECT last_value::bigint,is_called FROM {item.Sequence}", connection, transaction);
        await using var reader = await state.ExecuteReaderAsync();
        await reader.ReadAsync();
        long last = reader.GetInt64(0);
        bool called = reader.GetBoolean(1);
        if (maximum is not null && (called ? last < maximum : last <= maximum))
            throw new InvalidOperationException(
                $"A sequência {item.Sequence} está atrás de {table}.{column} (last_value={last}, max={maximum}). Corrija com setval antes de liberar o banco.");
    }
}

static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

static async Task WriteTextAtomicallyAsync(string outputPath, string content)
{
    string full = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    string temporary = $"{full}.{Guid.NewGuid():N}.tmp";
    try
    {
        await File.WriteAllTextAsync(temporary, content);
        File.Move(temporary, full, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}

static bool SidecarMatchesEmbedded(JsonElement sidecar, JsonElement embedded)
{
    JsonObject normalizedSidecar = JsonNode.Parse(sidecar.GetRawText())!.AsObject();
    JsonObject normalizedEmbedded = JsonNode.Parse(embedded.GetRawText())!.AsObject();
    normalizedSidecar.Remove("packageSha256");
    normalizedEmbedded.Remove("packageSha256");
    return JsonEquivalent(normalizedSidecar.ToJsonString(), normalizedEmbedded.ToJsonString());
}

static string CreateTemporaryFilePath(string operation)
{
    string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string baseDirectory = Path.Combine(
        string.IsNullOrWhiteSpace(localData) ? Path.GetTempPath() : localData,
        "FarmaFlow", "Migration", "temporary");
    Directory.CreateDirectory(baseDirectory);
    foreach (FileInfo stale in new DirectoryInfo(baseDirectory).GetFiles("*.dump"))
    {
        if (stale.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-1)) continue;
        try { stale.Delete(); } catch { }
    }
    return Path.Combine(baseDirectory, $"{operation}-{Guid.NewGuid():N}.dump");
}

static bool JsonElementsEquivalent(JsonElement left, JsonElement right)
{
    if (left.ValueKind != right.ValueKind)
        return false;

    return left.ValueKind switch
    {
        JsonValueKind.Object => JsonObjectsEquivalent(left, right),
        JsonValueKind.Array => JsonArraysEquivalent(left, right),
        JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
        JsonValueKind.Number => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
        JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false
    };
}

static bool JsonArraysEquivalent(JsonElement left, JsonElement right)
{
    JsonElement.ArrayEnumerator leftItems = left.EnumerateArray();
    JsonElement.ArrayEnumerator rightItems = right.EnumerateArray();
    while (leftItems.MoveNext())
    {
        if (!rightItems.MoveNext() || !JsonElementsEquivalent(leftItems.Current, rightItems.Current))
            return false;
    }
    return !rightItems.MoveNext();
}

static bool JsonObjectsEquivalent(JsonElement left, JsonElement right)
{
    Dictionary<string, JsonElement> leftProperties = left.EnumerateObject()
        .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
    Dictionary<string, JsonElement> rightProperties = right.EnumerateObject()
        .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
    return leftProperties.Count == rightProperties.Count && leftProperties.All(property =>
        rightProperties.TryGetValue(property.Key, out JsonElement rightValue) &&
        JsonElementsEquivalent(property.Value, rightValue));
}

static async Task<string?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    return Convert.ToString(await command.ExecuteScalarAsync());
}

static Dictionary<string, string> ParseArguments(IEnumerable<string> items)
{
    string[] values = items.ToArray();
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= values.Length)
            throw new InvalidOperationException($"Argumento inválido: {values[index]}");
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

static string Required(IReadOnlyDictionary<string, string> values, string name) =>
    values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"Informe --{name}.");

static SslMode ResolveSslMode(IReadOnlyDictionary<string, string> values, string host)
{
    bool loopback = host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    string configured = values.GetValueOrDefault("ssl-mode", loopback ? "Prefer" : "Require");
    return Enum.TryParse(configured, ignoreCase: true, out SslMode mode)
        ? mode
        : throw new InvalidOperationException($"ssl-mode inválido: {configured}.");
}

static string ReadSecret(string prompt) => FarmaFlow.Migration.ProcessSecretReader.Read(prompt);

static async Task<string> Sha256FileAsync(string path)
{
    await using FileStream input = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(input));
}
