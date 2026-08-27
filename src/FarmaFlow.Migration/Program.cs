using Npgsql;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const int formatVersion = 1;
byte[] magic = "FFMIGR1"u8.ToArray();

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("""
        FarmaFlow.Migration export-full --host HOST --port 5432 --database postgres --username USER --pg-bin DIR --output arquivo.ffbackup [--store-id UUID]
        FarmaFlow.Migration verify --input arquivo.ffbackup
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
    string sourcePassword = ReadSecret("Senha do PostgreSQL de origem: ");
    string packagePassword = ReadSecret("Senha do pacote criptografado: ");
    string confirmation = ReadSecret("Confirme a senha do pacote: ");
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(packagePassword), Encoding.UTF8.GetBytes(confirmation)))
        throw new InvalidOperationException("As senhas do pacote não coincidem.");

    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    string temporary = Path.Combine(Path.GetTempPath(), $"farmaflow-{Guid.NewGuid():N}.dump");
    var connectionBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = port,
        Database = database,
        Username = username,
        Password = sourcePassword,
        SslMode = SslMode.Require,
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
            "SELECT COALESCE(MAX(version), '0') FROM public.flyway_schema_history WHERE success") ?? "0";
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
        var counts = await ReadCountsAsync(connection, transaction);
        var reconciliation = await ReadReconciliationAsync(connection, transaction);
        await RunPgDumpAsync(pgBin, host, port, database, username, sourcePassword, snapshot, temporary);
        string restoreCatalog = await RunPgRestoreListAsync(pgBin, temporary);
        await transaction.RollbackAsync();

        byte[] plaintext = await File.ReadAllBytesAsync(temporary);
        string plaintextSha256 = Convert.ToHexString(SHA256.HashData(plaintext));
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(packagePassword, salt, 600_000, HashAlgorithmName.SHA256, 32);
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plaintext, ciphertext, magic);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);

        await using (var stream = File.Create(output))
        {
            await stream.WriteAsync(magic);
            await stream.WriteAsync(BitConverter.GetBytes(formatVersion));
            await stream.WriteAsync(salt);
            await stream.WriteAsync(nonce);
            await stream.WriteAsync(tag);
            await stream.WriteAsync(ciphertext);
        }

        var manifest = new
        {
            format = "FarmaFlow migration backup",
            formatVersion,
            kind = packageStoreId is null ? "FULL_ARCHIVE" : "STORE",
            storeId = packageStoreId,
            organizationId = packageOrganizationId,
            schema = "public",
            schemaVersion,
            databaseMajorVersion = 17,
            createdAt = DateTimeOffset.UtcNow,
            source = new { host, port, database },
            tables = counts,
            reconciliation,
            plaintextSha256,
            pgRestoreCatalogSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(restoreCatalog))),
            packageSha256 = await Sha256FileAsync(output)
        };
        await File.WriteAllTextAsync($"{output}.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
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
    byte[] payload = await File.ReadAllBytesAsync(input);
    if (payload.Length < magic.Length + 48 || !payload.AsSpan(0, magic.Length).SequenceEqual(magic))
        throw new InvalidDataException("O arquivo não é um pacote FarmaFlow válido.");
    int version = BitConverter.ToInt32(payload, magic.Length);
    if (version != formatVersion) throw new InvalidDataException($"Versão de pacote não suportada: {version}");

    string password = ReadSecret("Senha do pacote: ");
    int offset = magic.Length + sizeof(int);
    byte[] salt = payload.AsSpan(offset, 16).ToArray();
    byte[] nonce = payload.AsSpan(offset + 16, 12).ToArray();
    byte[] tag = payload.AsSpan(offset + 28, 16).ToArray();
    byte[] ciphertext = payload.AsSpan(offset + 44).ToArray();
    byte[] plaintext = new byte[ciphertext.Length];
    byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
    try
    {
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, magic);
        Console.WriteLine($"Pacote íntegro. Dump descriptografado: {plaintext.Length:N0} bytes; SHA-256 {Convert.ToHexString(SHA256.HashData(plaintext))}");
    }
    catch (CryptographicException)
    {
        throw new InvalidDataException("Senha incorreta ou pacote adulterado.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);
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
    string temporary = Path.Combine(Path.GetTempPath(), $"farmaflow-restore-{Guid.NewGuid():N}.dump");
    try
    {
        JsonDocument? expectedManifest = null;
        string manifestPath = $"{input}.json";
        if (File.Exists(manifestPath))
        {
            expectedManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            string expectedPackageHash = expectedManifest.RootElement.GetProperty("packageSha256").GetString() ?? string.Empty;
            if (!string.Equals(expectedPackageHash, await Sha256FileAsync(input), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O checksum do pacote não corresponde ao manifesto.");
        }

        byte[] plaintext = await DecryptAsync(input, packagePassword);
        await File.WriteAllBytesAsync(temporary, plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        await RunPgRestoreListAsync(pgBin, temporary);
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
            "SELECT COALESCE(MAX(version), '0') FROM public.flyway_schema_history WHERE success") ?? "0";
        if (!int.TryParse(version, out int numericVersion) || numericVersion < 52)
            throw new InvalidOperationException($"Schema restaurado na versão {version}; era esperada ao menos a V52.");
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
            if (!JsonEquivalent(expectedReconciliation, actualReconciliationJson))
                throw new InvalidOperationException("Os totais financeiros, de estoque, caixa ou sequências divergem da origem.");
        }
        await transaction.CommitAsync();
        Console.WriteLine($"Restauração concluída e validada no schema V{version}.");
    }
    finally
    {
        packagePassword = string.Empty;
        targetPassword = string.Empty;
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}

async Task<byte[]> DecryptAsync(string input, string password)
{
    byte[] payload = await File.ReadAllBytesAsync(input);
    if (payload.Length < magic.Length + 48 || !payload.AsSpan(0, magic.Length).SequenceEqual(magic))
        throw new InvalidDataException("O arquivo não é um pacote FarmaFlow válido.");
    int version = BitConverter.ToInt32(payload, magic.Length);
    if (version != formatVersion) throw new InvalidDataException($"Versão de pacote não suportada: {version}");
    int offset = magic.Length + sizeof(int);
    byte[] salt = payload.AsSpan(offset, 16).ToArray();
    byte[] nonce = payload.AsSpan(offset + 16, 12).ToArray();
    byte[] tag = payload.AsSpan(offset + 28, 16).ToArray();
    byte[] ciphertext = payload.AsSpan(offset + 44).ToArray();
    byte[] plaintext = new byte[ciphertext.Length];
    byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
    try
    {
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, magic);
        return plaintext;
    }
    catch (CryptographicException)
    {
        CryptographicOperations.ZeroMemory(plaintext);
        throw new InvalidDataException("Senha incorreta ou pacote adulterado.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
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
    string temporary = Path.Combine(Path.GetTempPath(), $"farmaflow-server-restore-{Guid.NewGuid():N}.dump");
    byte[] serverMagic = "FFBACKUP"u8.ToArray();
    try
    {
        byte[] payload = await File.ReadAllBytesAsync(input);
        if (payload.Length < serverMagic.Length + 32 || !payload.AsSpan(0, serverMagic.Length).SequenceEqual(serverMagic))
            throw new InvalidDataException("O arquivo não é um backup diário FarmaFlow válido.");
        int version = BitConverter.ToInt32(payload, serverMagic.Length);
        if (version != 1) throw new InvalidDataException($"Versão de backup não suportada: {version}");
        int offset = serverMagic.Length + sizeof(int);
        byte[] nonce = payload.AsSpan(offset, 12).ToArray();
        byte[] tag = payload.AsSpan(offset + 12, 16).ToArray();
        byte[] ciphertext = payload.AsSpan(offset + 28).ToArray();
        byte[] plaintext = new byte[ciphertext.Length];
        byte[] key = Convert.FromBase64String(recoveryKey);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, serverMagic);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Chave incorreta ou backup diário adulterado.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        await File.WriteAllBytesAsync(temporary, plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        await RunPgRestoreListAsync(pgBin, temporary);
        await RunPgRestoreAsync(pgBin, host, port, database, username, targetPassword, temporary);

        var connectionBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = database, Username = username,
            Password = targetPassword, SslMode = SslMode.Prefer, Timeout = 30, CommandTimeout = 0
        };
        await using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
        await connection.OpenAsync();
        string schemaVersion = await ScalarAsync(connection, null,
            "SELECT COALESCE(MAX(version), '0') FROM public.flyway_schema_history WHERE success") ?? "0";
        if (!int.TryParse(schemaVersion, out int numericVersion) || numericVersion < 52)
            throw new InvalidOperationException($"Backup restaurado no schema V{schemaVersion}; era esperada ao menos a V52.");
        Console.WriteLine($"Backup diário restaurado e validado no schema V{schemaVersion}.");
    }
    finally
    {
        recoveryKey = string.Empty;
        targetPassword = string.Empty;
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}

static async Task RunPgDumpAsync(string pgBin, string host, int port, string database, string username, string password, string snapshot, string output)
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
        "--exit-on-error", "--clean", "--if-exists", "--no-owner", "--no-acl", input
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
        ["stocktakes"] = "SELECT store_id, status, COUNT(*) AS records FROM public.stocktakes GROUP BY store_id,status ORDER BY store_id,status",
        ["sequences"] = "SELECT schemaname, sequencename, last_value FROM pg_catalog.pg_sequences WHERE schemaname='public' ORDER BY sequencename"
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
    return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
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

static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    var result = new StringBuilder();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && result.Length > 0) result.Length--;
        else if (!char.IsControl(key.KeyChar)) result.Append(key.KeyChar);
    }
    Console.WriteLine();
    return result.ToString();
}

static async Task<string> Sha256FileAsync(string path)
{
    await using FileStream input = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(input));
}
