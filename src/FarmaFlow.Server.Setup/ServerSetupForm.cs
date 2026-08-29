using FarmaFlow.Migration.Core;
using Npgsql;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
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
        footer.Controls.Add(_run); footer.Controls.Add(_next); footer.Controls.Add(_back);
        Controls.Add(_tabs); Controls.Add(footer);
        _tabs.SelectedIndexChanged += (_, _) => UpdateNavigation();
        _next.Click += (_, _) => { if (_tabs.SelectedIndex < 3) _tabs.SelectedIndex++; };
        _back.Click += (_, _) => { if (_tabs.SelectedIndex > 0) _tabs.SelectedIndex--; };
        _run.Click += RunButtonClick;
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
            _tabs.SelectedIndex = 3; _run.Enabled = false; _next.Enabled = false; _back.Enabled = false;
            _cancellation = new CancellationTokenSource();
            await ProvisionAsync(_cancellation.Token);
            Append("Servidor instalado e pronto para receber as estações.");
            _run.Enabled = true;
            MessageBox.Show(this, "Servidor instalado. Use o kit da estação para conectar os computadores de atendimento.", "Instalação concluída", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            try { await StopHostAsync(); } catch { }
            Append($"ERRO: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Não foi possível concluir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _run.Enabled = true;
        }
    }

    private async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        Append("Validando o pacote…");
        PackageEnvelope.PackageReadResult package = await PackageEnvelope.ReadAsync(_package.Text.Trim(), _password.Text, cancellationToken);
        if (package.Manifest is null) throw new InvalidDataException("O assistente precisa de um pacote .ffstore v2. Gere-o novamente pelo assistente de migração.");
        JsonElement root = package.Manifest.RootElement;
        if (!string.Equals(root.GetProperty("kind").GetString(), "STORE", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Selecione o pacote de uma loja, não o arquivo integral.");
        if (root.GetProperty("databaseMajorVersion").GetInt32() != 17) throw new InvalidDataException("O pacote não foi criado para PostgreSQL 17.");
        if (!int.TryParse(root.GetProperty("schemaVersion").GetString(), out int schema) || schema < 52) throw new InvalidDataException("O pacote precisa do schema V52 ou superior.");
        string installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string serviceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FarmaFlow", "Server");
        string secretsPath = Path.Combine(serviceRoot, "secrets.json");
        if (!File.Exists(secretsPath)) throw new InvalidOperationException("O PostgreSQL ainda não foi inicializado. Feche e abra o instalador novamente como administrador.");
        using JsonDocument secrets = JsonDocument.Parse(await File.ReadAllTextAsync(secretsPath, cancellationToken));
        string databasePassword = secrets.RootElement.GetProperty("DatabasePassword").GetString() ?? throw new InvalidOperationException("Senha local ausente.");
        await using (var checkConnection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 54329, Database = "farmaflow", Username = "farmaflow", Password = databasePassword, SslMode = SslMode.Prefer }.ConnectionString))
        {
            await checkConnection.OpenAsync(cancellationToken);
            await using var exists = new NpgsqlCommand("SELECT to_regclass('public.stores') IS NOT NULL", checkConnection);
            bool storesTableExists = Convert.ToBoolean(await exists.ExecuteScalarAsync(cancellationToken));
            long storeCount = 0;
            if (storesTableExists)
            {
                await using var check = new NpgsqlCommand("SELECT COUNT(*) FROM public.stores", checkConnection);
                storeCount = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken));
            }
            if (storeCount > 0)
                throw new InvalidOperationException("Este servidor já contém uma loja. A restauração foi bloqueada; use o modo Reparar para apenas verificar os serviços.");
        }
        string temporary = Path.Combine(Path.GetTempPath(), $"farmaflow-server-{Guid.NewGuid():N}.dump");
        try
        {
            await File.WriteAllBytesAsync(temporary, package.Plaintext, cancellationToken);
            string pgRestore = Path.Combine(installRoot, "runtime", "postgres", "bin", "pg_restore.exe");
            ProcessResult catalog = await RunAsync(pgRestore, ["--list", temporary], null, cancellationToken);
            EnsureSuccess(catalog, "O catálogo pg_restore do pacote é inválido.");
            if (root.TryGetProperty("pgRestoreCatalogSha256", out JsonElement expectedCatalog))
            {
                string actualCatalog = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(catalog.Output)));
                if (!string.Equals(expectedCatalog.GetString(), actualCatalog, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("O catálogo pg_restore não corresponde ao manifesto do pacote.");
            }
            ProcessResult restored = await RunAsync(pgRestore, ["--host", "127.0.0.1", "--port", "54329", "--username", "farmaflow", "--dbname", "farmaflow", "--exit-on-error", "--clean", "--if-exists", "--no-owner", "--no-acl", temporary], ["PGPASSWORD", databasePassword], cancellationToken);
            EnsureSuccess(restored, "A restauração do pacote falhou.");
            Append("Banco restaurado. Gravando configurações…");
            string configPath = Path.Combine(serviceRoot, "appsettings.local.json");
            var config = new Dictionary<string, object?> { ["ServerHost"] = new Dictionary<string, object?> { ["ExternalBackupDirectory"] = NullIfBlank(_backupDirectory.Text) } };
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            string activate = Path.Combine(installRoot, "installer", "activate-server.ps1");
            ProcessResult activated = await RunAsync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", activate, "-InstallDirectory", installRoot], null, cancellationToken);
            EnsureSuccess(activated, "A ativação do servidor falhou.");
            Append("Servidor ativado. Exportando a chave de recuperação…");
            string recoveryKey = secrets.RootElement.GetProperty("BackupKey").GetString() ?? throw new InvalidOperationException("Chave de backup ausente.");
            string recoveryPath = Path.Combine(_recoveryDirectory.Text.Trim(), $"farmaflow-recovery-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(recoveryPath, $"FarmaFlow — chave de recuperação\nServidor: {Environment.MachineName}\nBackupKey: {recoveryKey}\n", cancellationToken);
            Append($"Chave exportada para {recoveryPath}.");
            string hostExecutable = Path.Combine(installRoot, "FarmaFlowServerHost.exe");
            ProcessResult backup = await RunAsync(hostExecutable, ["--backup-once"], null, cancellationToken);
            EnsureSuccess(backup, "O primeiro backup local falhou.");
            string certificateFingerprint = await WaitForHostAsync(cancellationToken);
            await CreateStationKitAsync(serviceRoot, installRoot, databasePassword, certificateFingerprint, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(package.Plaintext); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task RepairAsync()
    {
        string installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        ProcessResult activated = await RunAsync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(installRoot, "installer", "activate-server.ps1"), "-InstallDirectory", installRoot], null, CancellationToken.None);
        EnsureSuccess(activated, "A verificação do servidor falhou.");
        await WaitForHostAsync(CancellationToken.None);
    }

    private static async Task StopHostAsync()
    {
        using Process process = Process.Start(new ProcessStartInfo("sc.exe", ["stop", "FarmaFlowServer"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Não foi possível parar o Host.");
        await process.WaitForExitAsync();
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
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken); Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken); return new ProcessResult(process.ExitCode, await output, await error);
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
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);
