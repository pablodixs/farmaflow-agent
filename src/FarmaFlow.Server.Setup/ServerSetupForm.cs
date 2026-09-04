using FarmaFlow.Migration.Core;
using Npgsql;
using System.Diagnostics;
using System.Data;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace FarmaFlow.Server.Setup;

internal sealed class ServerSetupForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(18, 6) };
    private readonly TextBox _package = new() { PlaceholderText = "Selecione o arquivo .ffstore" };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _backupDirectory = new() { PlaceholderText = "Opcional: D:\\FarmaFlow-Backups" };
    private readonly TextBox _recoveryDirectory = new() { PlaceholderText = "Selecione uma mídia separada" };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button _run = new() { Text = "Instalar servidor", AutoSize = true };
    private readonly Button _next = new() { Text = "Continuar", AutoSize = true };
    private readonly Button _back = new() { Text = "Voltar", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancelar", AutoSize = true, Visible = false };
    private readonly string _diagnosticPath;
    private CancellationTokenSource? _cancellation;

    internal ServerSetupForm()
    {
        string diagnosticDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "FarmaFlow", "Server", "logs");
        Directory.CreateDirectory(diagnosticDirectory);
        _diagnosticPath = Path.Combine(diagnosticDirectory, $"server-setup-{DateTime.Now:yyyyMMdd}.log");
        Text = "FarmaFlow — Instalar servidor";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 520);
        Size = new Size(900, 620);
        BuildPages();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 8) };
        footer.Controls.Add(_run); footer.Controls.Add(_cancel); footer.Controls.Add(_next); footer.Controls.Add(_back);
        Controls.Add(_tabs); Controls.Add(footer);
        _tabs.SelectedIndexChanged += (_, _) => UpdateNavigation();
        _next.Click += (_, _) => { if (_tabs.SelectedIndex < 3) _tabs.SelectedIndex++; };
        _back.Click += (_, _) => { if (_tabs.SelectedIndex > 0) _tabs.SelectedIndex--; };
        _run.Click += RunButtonClick;
        _cancel.Click += (_, _) => { _cancel.Enabled = false; _cancellation?.Cancel(); Append("Cancelamento solicitado; encerrando o processo em execução…"); };
        UpdateNavigation();
    }

    private void BuildPages()
    {
        var welcome = PagePanel();
        welcome.Controls.Add(new Label { Text = "Instalar o servidor desta loja", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 16) });
        welcome.Controls.Add(new Label { Text = "O assistente prepara o banco, restaura o pacote e ativa o servidor. Instalações já operacionais não são sobrescritas.", AutoSize = true, Dock = DockStyle.Top });
        _tabs.TabPages.Add(new TabPage("1 · Início") { Controls = { welcome } });

        var packagePage = PagePanel();
        AddField(packagePage, "Pacote da loja (.ffstore)", _package);
        var selectPackage = new Button { Text = "Procurar…", AutoSize = true };
        selectPackage.Click += (_, _) => SelectFile(_package, "Pacote FarmaFlow|*.ffstore;*.ffbackup|Todos os arquivos|*.*");
        packagePage.Controls.Add(selectPackage);
        AddField(packagePage, "Senha do corte", _password);
        _tabs.TabPages.Add(new TabPage("2 · Pacote") { Controls = { packagePage } });

        var backupPage = PagePanel();
        AddField(backupPage, "Pasta externa dos backups", _backupDirectory);
        var selectBackup = new Button { Text = "Escolher pasta…", AutoSize = true };
        selectBackup.Click += (_, _) => SelectFolder(_backupDirectory);
        backupPage.Controls.Add(selectBackup);
        AddField(backupPage, "Pasta para a chave de recuperação", _recoveryDirectory);
        var selectRecovery = new Button { Text = "Escolher pasta…", AutoSize = true };
        selectRecovery.Click += (_, _) => SelectFolder(_recoveryDirectory);
        backupPage.Controls.Add(selectRecovery);
        backupPage.Controls.Add(new Label { Text = "Use uma mídia separada do backup. A instalação só será marcada como pronta depois que a chave for exportada.", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 16, 0, 0) });
        _tabs.TabPages.Add(new TabPage("3 · Backup") { Controls = { backupPage } });
        _tabs.TabPages.Add(new TabPage("4 · Resultado") { Controls = { _log } });
    }

    private async Task InstallAsync()
    {
        if (_tabs.SelectedIndex != 2) { _tabs.SelectedIndex = 2; return; }
        try
        {
            if (Environment.GetCommandLineArgs().Any(argument => string.Equals(argument, "--repair", StringComparison.OrdinalIgnoreCase)))
            {
                _tabs.SelectedIndex = 3; _run.Enabled = false; _next.Enabled = false; _back.Enabled = false;
                await RepairAsync();
                Append("Verificação de reparo concluída.");
                _run.Enabled = true;
                return;
            }
            if (!File.Exists(_package.Text.Trim())) throw new InvalidOperationException("Selecione um pacote .ffstore válido.");
            if (_password.Text.Length < 12) throw new InvalidOperationException("Informe a senha do corte.");
            if (string.IsNullOrWhiteSpace(_backupDirectory.Text)) throw new InvalidOperationException("Escolha uma pasta externa para os backups.");
            if (string.IsNullOrWhiteSpace(_recoveryDirectory.Text) || !Directory.Exists(_recoveryDirectory.Text)) throw new InvalidOperationException("Escolha uma pasta para guardar a chave de recuperação.");
            string? backupRoot = Path.GetPathRoot(Path.GetFullPath(_backupDirectory.Text.Trim()));
            string? recoveryRoot = Path.GetPathRoot(Path.GetFullPath(_recoveryDirectory.Text.Trim()));
            if (string.Equals(backupRoot, recoveryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A chave de recuperação precisa ficar em outra unidade ou compartilhamento, separada do disco de backup.");
            _tabs.SelectedIndex = 3; _run.Enabled = false; _next.Enabled = false; _back.Enabled = false; _cancel.Visible = true; _cancel.Enabled = true;
            _cancellation = new CancellationTokenSource();
            await ProvisionAsync(_cancellation.Token);
            Append("Servidor instalado e pronto para receber as estações.");
            _run.Enabled = true;
            MessageBox.Show(this, "Servidor instalado. Use o kit da estação para conectar os computadores de atendimento.", "Instalação concluída", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            try { await StopHostAsync(); } catch { }
            Append("Instalação cancelada. Os dados temporários foram removidos; execute o instalador novamente para retomar.");
            MessageBox.Show(this, "Instalação cancelada com segurança. Execute novamente para retomar.", "Instalação cancelada", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _run.Enabled = true;
            _next.Enabled = true;
            _back.Enabled = true;
            _tabs.SelectedIndex = 2;
        }
        catch (Exception exception)
        {
            try { await StopHostAsync(); } catch { }
            Append($"ERRO: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Não foi possível concluir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _run.Enabled = true;
            _next.Enabled = true;
            _back.Enabled = true;
            _tabs.SelectedIndex = 2;
        }
        finally
        {
            _password.Text = string.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
            _cancel.Visible = false;
        }
    }

    private async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        Append("Validando o pacote…");
        await ValidateWritableDirectoryAsync(_backupDirectory.Text.Trim(), "backup externo", create: true, cancellationToken);
        await ValidateWritableDirectoryAsync(_recoveryDirectory.Text.Trim(), "chave de recuperação", create: false, cancellationToken);
        string installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string serviceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FarmaFlow", "Server");
        string temporaryRoot = Path.Combine(serviceRoot, "temporary");
        Directory.CreateDirectory(temporaryRoot);
        await ProtectDirectoryAsync(temporaryRoot, cancellationToken);
        CleanupStaleTemporaryFiles(temporaryRoot);
        EnsureFreeSpace(temporaryRoot, checked(new FileInfo(_package.Text.Trim()).Length * 2L));
        string temporary = Path.Combine(temporaryRoot, $"migration-{Guid.NewGuid():N}.dump");
        JsonDocument? manifest = null;
        try
        {
            PackageEnvelope.PackageExtractResult package = await PackageEnvelope.ExtractAsync(_package.Text.Trim(), temporary, _password.Text, cancellationToken);
            manifest = package.Manifest ?? throw new InvalidDataException("O pacote é legado e não contém manifesto autenticado. Gere novamente o .ffstore pelo assistente de migração atual.");
            JsonElement root = manifest.RootElement;
            if (!string.Equals(root.GetProperty("kind").GetString(), "STORE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selecione o pacote de uma loja, não o arquivo integral.");
            int databaseMajor = root.TryGetProperty("targetDatabaseMajorVersion", out JsonElement targetVersion)
                ? targetVersion.GetInt32()
                : root.GetProperty("databaseMajorVersion").GetInt32();
            if (databaseMajor != 17) throw new InvalidDataException($"O pacote requer PostgreSQL {databaseMajor}; este instalador contém PostgreSQL 17.");
            if (!int.TryParse(root.GetProperty("schemaVersion").GetString(), out int schema) || schema is < 52 or > 54)
                throw new InvalidDataException($"Schema V{root.GetProperty("schemaVersion")} incompatível. Este instalador suporta somente V52 a V54.");
            Guid expectedStoreId = root.GetProperty("storeId").GetGuid();

            string pgRestore = Path.Combine(installRoot, "runtime", "postgres", "bin", "pg_restore.exe");
            ProcessResult catalog = await RunAsync(pgRestore, ["--list", temporary], null, cancellationToken);
            EnsureSuccess(catalog, "O catálogo pg_restore do pacote é inválido.");
            if (root.TryGetProperty("pgRestoreCatalogSha256", out JsonElement expectedCatalog))
            {
                string actualCatalog = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(catalog.Output)));
                if (!string.Equals(expectedCatalog.GetString(), actualCatalog, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("O catálogo pg_restore não corresponde ao manifesto autenticado. Gere ou transfira o pacote novamente.");
            }

            string secretsPath = Path.Combine(serviceRoot, "secrets.json");
            if (!File.Exists(secretsPath)) throw new InvalidOperationException("O PostgreSQL ainda não foi inicializado. Feche e abra o instalador novamente como administrador.");
            using JsonDocument secrets = JsonDocument.Parse(await File.ReadAllTextAsync(secretsPath, cancellationToken));
            string databasePassword = secrets.RootElement.GetProperty("DatabasePassword").GetString() ?? throw new InvalidOperationException("Senha local ausente.");
            await ValidateLocalPostgresVersionAsync(databasePassword, databaseMajor, cancellationToken);
            string migrationMarker = Path.Combine(serviceRoot, "migration-required.txt");
            await ValidateExistingInstallationAsync(databasePassword, expectedStoreId, migrationMarker, cancellationToken);
            await EnsureRequiredExtensionsAsync(databasePassword, root, catalog.Output, cancellationToken);

            await StopHostAsync(cancellationToken);
            Append("Restaurando o banco em uma transação atômica…");
            ProcessResult restored = await RunAsync(pgRestore,
                ["--host", "127.0.0.1", "--port", "54329", "--username", "farmaflow", "--dbname", "farmaflow", "--exit-on-error", "--single-transaction", "--clean", "--if-exists", "--no-owner", "--no-acl", temporary],
                ["PGPASSWORD", databasePassword], cancellationToken);
            EnsureSuccess(restored, "A restauração falhou e foi revertida integralmente; o banco anterior foi preservado.");
            await ValidateRestoredDatabaseAsync(databasePassword, root, expectedStoreId, cancellationToken);

            Append("Banco restaurado e reconciliado. Gravando configurações…");
            string configPath = Path.Combine(serviceRoot, "appsettings.local.json");
            var config = new Dictionary<string, object?> { ["ServerHost"] = new Dictionary<string, object?> { ["ExternalBackupDirectory"] = NullIfBlank(_backupDirectory.Text) } };
            await WriteTextAtomicallyAsync(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            string activate = Path.Combine(installRoot, "installer", "activate-server.ps1");
            ProcessResult activated = await RunAsync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", activate, "-InstallDirectory", installRoot, "-KeepMigrationMarker"], null, cancellationToken);
            EnsureSuccess(activated, "A ativação do servidor falhou. Execute novamente; a restauração pode ser retomada.");
            Append("Servidor ativado. Exportando a chave de recuperação…");
            string recoveryKey = secrets.RootElement.GetProperty("BackupKey").GetString() ?? throw new InvalidOperationException("Chave de backup ausente.");
            string recoveryPath = Path.Combine(_recoveryDirectory.Text.Trim(), $"farmaflow-recovery-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await WriteTextAtomicallyAsync(recoveryPath, $"FarmaFlow — chave de recuperação\nServidor: {Environment.MachineName}\nBackupKey: {recoveryKey}\n", cancellationToken);
            Append($"Chave exportada para {recoveryPath}.");
            string hostExecutable = Path.Combine(installRoot, "FarmaFlowServerHost.exe");
            ProcessResult backup = await RunAsync(hostExecutable, ["--backup-once"], null, cancellationToken);
            EnsureSuccess(backup, "O primeiro backup local falhou. Corrija a pasta de backup e execute o instalador novamente.");
            string certificateFingerprint = await WaitForHostAsync(cancellationToken);
            await CreateStationKitAsync(serviceRoot, installRoot, databasePassword, certificateFingerprint, cancellationToken);
            await WriteTextAtomicallyAsync(Path.Combine(serviceRoot, "installation-complete.json"), JsonSerializer.Serialize(new
            {
                completedAt = DateTimeOffset.UtcNow,
                storeId = expectedStoreId,
                schemaVersion = schema,
                packageSha256 = await PackageEnvelope.Sha256FileAsync(_package.Text.Trim(), cancellationToken)
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Delete(migrationMarker);
        }
        finally
        {
            manifest?.Dispose();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task ValidateExistingInstallationAsync(string databasePassword, Guid expectedStoreId, string migrationMarker, CancellationToken cancellationToken)
    {
        string completionMarker = Path.Combine(Path.GetDirectoryName(migrationMarker)!, "installation-complete.json");
        if (File.Exists(completionMarker))
            throw new InvalidOperationException("Este servidor já concluiu a instalação. Para proteger dados operacionais, use Reparar; o pacote de corte não será restaurado novamente.");
        await using var connection = await OpenDatabaseAsync(databasePassword, cancellationToken);
        await using var exists = new NpgsqlCommand("SELECT to_regclass('public.stores') IS NOT NULL", connection);
        if (!Convert.ToBoolean(await exists.ExecuteScalarAsync(cancellationToken))) return;

        var storeIds = new List<Guid>();
        await using (var stores = new NpgsqlCommand("SELECT id FROM public.stores ORDER BY id LIMIT 2", connection))
        await using (var reader = await stores.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) storeIds.Add(reader.GetGuid(0));
        if (storeIds.Count == 0) return;
        if (!File.Exists(migrationMarker))
            throw new InvalidOperationException("Este servidor já está operacional. Para proteger vendas posteriores ao corte, o instalador não sobrescreve o banco; use Reparar ou restaure um backup pelo procedimento de contingência.");
        if (storeIds.Count != 1 || storeIds[0] != expectedStoreId)
            throw new InvalidOperationException($"A instalação interrompida contém uma loja diferente do pacote ({string.Join(", ", storeIds)}). Recrie o banco local vazio antes de tentar novamente.");
        Append("Instalação anterior incompleta detectada para a mesma loja. A restauração será refeita de forma atômica.");
    }

    private static async Task EnsureRequiredExtensionsAsync(string databasePassword, JsonElement manifest, string restoreCatalog, CancellationToken cancellationToken)
    {
        var extensions = new List<(string Name, string Schema)>();
        if (manifest.TryGetProperty("extensions", out JsonElement values))
        {
            foreach (JsonElement item in values.EnumerateArray())
                extensions.Add((item.GetProperty("name").GetString() ?? string.Empty, item.GetProperty("schema").GetString() ?? "public"));
        }
        else if (!restoreCatalog.Contains(" EXTENSION - pg_trgm ", StringComparison.OrdinalIgnoreCase))
        {
            extensions.Add(("pg_trgm", "public"));
        }

        await using var connection = await OpenDatabaseAsync(databasePassword, cancellationToken);
        foreach ((string name, string schema) in extensions)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(schema))
                throw new InvalidDataException("O manifesto autenticado contém uma extensão PostgreSQL inválida.");
            if (restoreCatalog.Contains($" EXTENSION - {name} ", StringComparison.OrdinalIgnoreCase)) continue;
            await using var command = new NpgsqlCommand(
                $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schema)}; CREATE EXTENSION IF NOT EXISTS {QuoteIdentifier(name)} WITH SCHEMA {QuoteIdentifier(schema)}", connection);
            try { await command.ExecuteNonQueryAsync(cancellationToken); }
            catch (PostgresException exception)
            {
                throw new InvalidOperationException($"A extensão PostgreSQL '{name}' exigida pelo pacote não está disponível no runtime. Instale uma distribuição PostgreSQL 17 que inclua a extensão e execute novamente.", exception);
            }
        }
    }

    private static async Task ValidateRestoredDatabaseAsync(string databasePassword, JsonElement manifest, Guid expectedStoreId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseAsync(databasePassword, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);

        long failed = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM public.flyway_schema_history WHERE NOT success", cancellationToken));
        if (failed != 0) throw new InvalidOperationException($"O banco restaurado contém {failed} migration(s) Flyway com falha. Gere o pacote novamente depois de executar flyway repair na origem.");
        string version = Convert.ToString(await ScalarAsync(connection, transaction,
            "SELECT COALESCE((SELECT version FROM public.flyway_schema_history WHERE success ORDER BY installed_rank DESC LIMIT 1), '0')", cancellationToken)) ?? "0";
        if (!int.TryParse(version, out int numericVersion) || numericVersion is < 52 or > 54)
            throw new InvalidOperationException($"O banco restaurado ficou no schema V{version}; esta release suporta somente V52 a V54.");

        var storeIds = new List<Guid>();
        await using (var stores = new NpgsqlCommand("SELECT id FROM public.stores ORDER BY id LIMIT 2", connection, transaction))
        await using (var reader = await stores.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) storeIds.Add(reader.GetGuid(0));
        if (storeIds.Count != 1 || storeIds[0] != expectedStoreId)
            throw new InvalidOperationException($"O pacote deveria conter somente a loja {expectedStoreId}, mas a restauração retornou: {(storeIds.Count == 0 ? "nenhuma loja" : string.Join(", ", storeIds))}.");

        SortedDictionary<string, long> actualCounts = await ReadCountsAsync(connection, transaction, cancellationToken);
        if (manifest.TryGetProperty("tables", out JsonElement expectedTables))
        {
            var expectedCounts = new SortedDictionary<string, long>(
                expectedTables.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetInt64(), StringComparer.Ordinal),
                StringComparer.Ordinal);
            string[] differences = expectedCounts.Keys.Union(actualCounts.Keys, StringComparer.Ordinal)
                .Where(table => !expectedCounts.TryGetValue(table, out long expected) || !actualCounts.TryGetValue(table, out long actual) || expected != actual)
                .Take(10)
                .Select(table => $"{table}: esperado {expectedCounts.GetValueOrDefault(table)}, restaurado {actualCounts.GetValueOrDefault(table)}")
                .ToArray();
            if (differences.Length != 0)
                throw new InvalidOperationException($"As contagens do banco restaurado divergem do pacote: {string.Join("; ", differences)}. Não ative o servidor; gere ou transfira o pacote novamente.");
        }

        SortedDictionary<string, JsonElement> actualReconciliation = await ReadReconciliationAsync(connection, transaction, cancellationToken);
        if (manifest.TryGetProperty("reconciliation", out JsonElement expectedReconciliation))
        {
            JsonObject expected = JsonNode.Parse(expectedReconciliation.GetRawText())!.AsObject();
            JsonObject actual = JsonNode.Parse(JsonSerializer.Serialize(actualReconciliation))!.AsObject();
            expected.Remove("sequences");
            actual.Remove("sequences");
            string[] differences = expected.Select(item => item.Key).Union(actual.Select(item => item.Key), StringComparer.Ordinal)
                .Where(name => !expected.TryGetPropertyValue(name, out JsonNode? left) || !actual.TryGetPropertyValue(name, out JsonNode? right)
                    || !JsonEquivalent(left, right))
                .ToArray();
            if (differences.Length != 0)
                throw new InvalidOperationException($"A reconciliação divergiu nas áreas: {string.Join(", ", differences)}. O servidor não será ativado; gere o pacote novamente e investigue os dados da origem.");
        }

        if (Convert.ToBoolean(await ScalarAsync(connection, transaction, "SELECT to_regclass('public.local_media_blobs') IS NOT NULL", cancellationToken)))
        {
            long missing = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM public.local_media_blobs WHERE missing OR content IS NULL", cancellationToken));
            if (missing != 0)
                throw new InvalidOperationException($"O pacote contém {missing} mídia(s) ausente(s). Volte ao staging, corrija as URLs/acesso e execute archive-media novamente.");
        }
        await ValidateSequencesAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<SortedDictionary<string, long>> ReadCountsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname='public' ORDER BY tablename", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{QuoteIdentifier(table)}", connection, transaction);
            result[table] = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        return result;
    }

    private static async Task<SortedDictionary<string, JsonElement>> ReadReconciliationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
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
        if (Convert.ToBoolean(await ScalarAsync(connection, transaction, "SELECT to_regclass('public.local_media_blobs') IS NOT NULL", cancellationToken)))
            queries["media"] = "SELECT media_id,missing,sha256,source_url,failure FROM public.local_media_blobs ORDER BY media_id";
        var result = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string name, string query) in queries)
        {
            object? value = await ScalarAsync(connection, transaction, $"SELECT COALESCE(jsonb_agg(row_to_json(record)), '[]'::jsonb)::text FROM ({query}) record", cancellationToken);
            using JsonDocument document = JsonDocument.Parse(Convert.ToString(value) ?? "[]");
            result[name] = document.RootElement.Clone();
        }
        return result;
    }

    private static async Task ValidateSequencesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname,c.relname,a.attname,pg_get_serial_sequence(format('%I.%I',n.nspname,c.relname),a.attname)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE n.nspname='public' AND pg_get_serial_sequence(format('%I.%I',n.nspname,c.relname),a.attname) IS NOT NULL
            """;
        var sequences = new List<(string Schema, string Table, string Column, string Sequence)>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) sequences.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        foreach (var item in sequences)
        {
            string table = $"{QuoteIdentifier(item.Schema)}.{QuoteIdentifier(item.Table)}";
            object? maximumValue = await ScalarAsync(connection, transaction, $"SELECT MAX({QuoteIdentifier(item.Column)})::bigint FROM {table}", cancellationToken);
            if (maximumValue is null or DBNull) continue;
            long maximum = Convert.ToInt64(maximumValue);
            await using var state = new NpgsqlCommand($"SELECT last_value::bigint,is_called FROM {item.Sequence}", connection, transaction);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException($"Não foi possível ler a sequência {item.Sequence}.");
            long last = reader.GetInt64(0);
            bool called = reader.GetBoolean(1);
            if (called ? last < maximum : last <= maximum)
                throw new InvalidOperationException($"A sequência {item.Sequence} está atrás dos dados (last_value={last}, maior id={maximum}). Execute setval na origem e gere o pacote novamente.");
        }
    }

    private static async Task<NpgsqlConnection> OpenDatabaseAsync(string databasePassword, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = 54329, Database = "farmaflow", Username = "farmaflow",
            Password = databasePassword, SslMode = SslMode.Prefer, Timeout = 30, CommandTimeout = 0
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ValidateLocalPostgresVersionAsync(string databasePassword, int expectedMajor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseAsync(databasePassword, cancellationToken);
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        string raw = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "0";
        if (!int.TryParse(raw, out int versionNumber) || versionNumber / 10_000 != expectedMajor)
            throw new InvalidOperationException($"O pacote requer PostgreSQL {expectedMajor}, mas o serviço local informou server_version_num={raw}. Reinstale o runtime PostgreSQL correto.");
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static bool JsonEquivalent(JsonNode? left, JsonNode? right)
    {
        return JsonNode.DeepEquals(left, right);
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static async Task ProtectDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync("icacls.exe", [path, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)(F)", "*S-1-5-32-544:(OI)(CI)(F)"], null, cancellationToken);
        EnsureSuccess(result, "Não foi possível proteger a pasta temporária com a ACL do Windows.");
    }

    private static void CleanupStaleTemporaryFiles(string directory)
    {
        foreach (FileInfo file in new DirectoryInfo(directory).GetFiles("migration-*.dump"))
            if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                try { file.Delete(); } catch { }
    }

    private static void EnsureFreeSpace(string directory, long requiredBytes)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? throw new InvalidOperationException("Não foi possível identificar o disco temporário.");
        long available = new DriveInfo(root).AvailableFreeSpace;
        if (available < requiredBytes)
            throw new IOException($"Espaço insuficiente para validar o pacote. Necessário: {requiredBytes / 1024 / 1024:N0} MB; disponível: {available / 1024 / 1024:N0} MB. Libere espaço no disco {root} e tente novamente.");
    }

    private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task ValidateWritableDirectoryAsync(string path, string description, bool create, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(path);
        try
        {
            if (create) Directory.CreateDirectory(full);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
            string probe = Path.Combine(full, $".farmaflow-write-test-{Guid.NewGuid():N}.tmp");
            try { await File.WriteAllBytesAsync(probe, [0x46, 0x46], cancellationToken); }
            finally { if (File.Exists(probe)) File.Delete(probe); }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new IOException($"A pasta de {description} não está disponível para escrita: {full}. Conecte a unidade, ajuste as permissões e tente novamente.", exception);
        }
    }

    private async Task RepairAsync()
    {
        string installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        ProcessResult activated = await RunAsync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(installRoot, "installer", "activate-server.ps1"), "-InstallDirectory", installRoot], null, CancellationToken.None);
        EnsureSuccess(activated, "A verificação do servidor falhou.");
        await WaitForHostAsync(CancellationToken.None);
    }

    private static async Task StopHostAsync(CancellationToken cancellationToken = default)
    {
        const string command = "$service=Get-Service -Name 'FarmaFlowServer' -ErrorAction SilentlyContinue; if($null -ne $service -and $service.Status -ne 'Stopped'){Stop-Service -Name 'FarmaFlowServer' -Force -ErrorAction Stop; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))}";
        ProcessResult result = await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], null, cancellationToken);
        EnsureSuccess(result, "O FarmaFlow Server não pôde ser parado antes da restauração.");
    }

    private static async Task<string> WaitForHostAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }) { Timeout = TimeSpan.FromSeconds(5) };
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage health = await client.GetAsync("https://127.0.0.1:8443/.well-known/farmaflow/health", cancellationToken);
                using HttpResponseMessage deployment = await client.GetAsync("https://127.0.0.1:8443/backend/public/deployment", cancellationToken);
                using HttpResponseMessage web = await client.GetAsync("https://127.0.0.1:8443/", cancellationToken);
                using HttpResponseMessage server = await client.GetAsync("https://127.0.0.1:8443/.well-known/farmaflow/server", cancellationToken);
                string healthBody = await health.Content.ReadAsStringAsync(cancellationToken);
                string deploymentBody = await deployment.Content.ReadAsStringAsync(cancellationToken);
                if (health.IsSuccessStatusCode
                    && healthBody.Contains("\"UP\"", StringComparison.OrdinalIgnoreCase)
                    && deployment.IsSuccessStatusCode
                    && deploymentBody.Contains("LOCAL_SINGLE_STORE", StringComparison.OrdinalIgnoreCase)
                    && web.IsSuccessStatusCode
                    && server.IsSuccessStatusCode)
                {
                    using JsonDocument serverInfo = JsonDocument.Parse(await server.Content.ReadAsStringAsync(cancellationToken));
                    return serverInfo.RootElement.GetProperty("certificateSha256").GetString()
                        ?? throw new InvalidDataException("O Host não informou a impressão digital do certificado.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
            }
            await Task.Delay(2000, cancellationToken);
        }
        throw new InvalidOperationException($"O servidor foi ativado, mas Spring, Next e Host não concluíram os testes em até dois minutos. {Redact(lastError?.Message ?? string.Empty)}");
    }

    private async Task CreateStationKitAsync(string serviceRoot, string installRoot, string databasePassword, string certificateFingerprint, CancellationToken cancellationToken)
    {
        string stationInstaller = Path.Combine(installRoot, "FarmaFlow-Estacao-Setup.exe");
        if (!File.Exists(stationInstaller)) throw new InvalidOperationException("O instalador da estação não foi incluído nesta release.");
        var builder = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 54329, Database = "farmaflow", Username = "farmaflow", Password = databasePassword, SslMode = SslMode.Prefer };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id::text,name FROM public.stores LIMIT 2", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("O servidor precisa conter exatamente uma loja para gerar o kit.");
        string storeId = reader.GetString(0);
        string storeName = reader.GetString(1);
        if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("O servidor precisa conter exatamente uma loja para gerar o kit.");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        X509Certificate2? certificate = store.Certificates.OfType<X509Certificate2>().FirstOrDefault(item =>
            item.FriendlyName == "FarmaFlow Local Server"
            && item.HasPrivateKey
            && string.Equals(Convert.ToHexString(SHA256.HashData(item.RawData)), certificateFingerprint, StringComparison.OrdinalIgnoreCase));
        if (certificate is null) throw new InvalidOperationException("Certificado local do servidor não encontrado.");
        string kit = Path.Combine(serviceRoot, "station-kit");
        Directory.CreateDirectory(kit);
        File.Copy(stationInstaller, Path.Combine(kit, Path.GetFileName(stationInstaller)), overwrite: true);
        string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        string stationFile = Path.Combine(kit, $"{Sanitize(storeName)}.ffstation");
        await StationBootstrapPackage.WriteAsync(stationFile, new StationBootstrapInfo(
            $"https://{Environment.MachineName}:8443", Environment.MachineName, storeId, storeName, fingerprint,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)), certificate, cancellationToken);
        string instructions = $"1. Instale FarmaFlow-Estacao-Setup.exe.\r\n2. Abra {Path.GetFileName(stationFile)} e confirme \"Conectar esta estação à {storeName}?\".\r\n";
        await File.WriteAllTextAsync(Path.Combine(kit, "LEIA-ME.txt"), instructions, cancellationToken);
        Append($"Kit da estação criado em {kit}.");
    }

    private static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, IReadOnlyList<string>? environment, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null && environment.Count == 2) info.Environment[environment[0]] = environment[1];
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Não foi possível iniciar {Path.GetFileName(executable)}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            }
            throw;
        }
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static void EnsureSuccess(ProcessResult result, string message) { if (result.ExitCode != 0) throw new InvalidOperationException($"{message} {Redact(result.Error)}"); }
    private static string Redact(string value)
    {
        string redacted = Regex.Replace(value, "(?i)(password|passwd|pwd|user|username|token)=\\S+", "$1=[redacted]");
        return redacted.Length <= 400 ? redacted : redacted[..400];
    }
    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Sanitize(string value)
    {
        string result = string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')).Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "loja" : result;
    }
    private static FlowLayoutPanel PagePanel() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(28) };
    private static void AddField(Control parent, string label, Control field) { parent.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 0, 4) }); field.Width = 620; parent.Controls.Add(field); }
    private static void SelectFile(TextBox target, string filter) { using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true }; if (dialog.ShowDialog() == DialogResult.OK) target.Text = dialog.FileName; }
    private static void SelectFolder(TextBox target) { using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog() == DialogResult.OK) target.Text = dialog.SelectedPath; }
    private async void RunButtonClick(object? sender, EventArgs e) { if (_tabs.SelectedIndex == 3) { Close(); return; } await InstallAsync(); }
    private void UpdateNavigation() { _back.Enabled = _tabs.SelectedIndex is > 0 and < 3; _next.Visible = _tabs.SelectedIndex is 0 or 1 or 2; _run.Visible = _tabs.SelectedIndex is 2 or 3; _run.Text = _tabs.SelectedIndex == 2 ? "Instalar servidor" : "Fechar"; }
    private void Append(string message)
    {
        string line = $"{DateTime.Now:O}  {Redact(message)}{Environment.NewLine}";
        _log.AppendText(line);
        try { File.AppendAllText(_diagnosticPath, line); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cancellation is not null)
        {
            e.Cancel = true;
            MessageBox.Show(this, "A instalação ainda está em andamento. Clique em Cancelar e aguarde a limpeza antes de fechar.", "Instalação em andamento", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        base.OnFormClosing(e);
    }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);
