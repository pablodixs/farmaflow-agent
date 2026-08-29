using FarmaFlow.Migration.Core;
using Npgsql;

namespace FarmaFlow.Migration.Desktop;

internal sealed class MigrationWizardForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(18, 6) };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _connection = new() { PlaceholderText = "postgresql://usuario@host:5432/postgres" };
    private readonly TextBox _sourcePassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _packagePassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _publicApiKey = new() { UseSystemPasswordChar = true, PlaceholderText = "Chave pública anon (somente para o teste)" };
    private readonly TextBox _output = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FarmaFlow-Corte") };
    private readonly CheckedListBox _stores = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly CheckBox _maintenance = new() { Text = "O backend cloud está em manutenção e nenhuma gravação está ocorrendo.", AutoSize = true };
    private readonly CheckBox _dataApi = new() { Text = "Removi public dos schemas expostos, testei a chamada REST negada e confirmei login/API Spring.", AutoSize = true };
    private readonly Button _next = new() { Text = "Continuar", AutoSize = true };
    private readonly Button _back = new() { Text = "Voltar", AutoSize = true, Enabled = false };
    private readonly Button _run = new() { Text = "Gerar pacotes", AutoSize = true };
    private IReadOnlyList<StoreChoice> _discoveredStores = [];
    private CancellationTokenSource? _cancellation;

    internal MigrationWizardForm()
    {
        Text = "FarmaFlow — Preparar migração";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 560);
        Size = new Size(920, 650);
        _mode.Items.AddRange(["Ensaio com cópia do Supabase", "Corte definitivo"]);
        _mode.SelectedIndex = 0;
        BuildPages();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 8) };
        footer.Controls.Add(_run); footer.Controls.Add(_next); footer.Controls.Add(_back);
        Controls.Add(_tabs); Controls.Add(footer);
        _tabs.SelectedIndexChanged += (_, _) => UpdateNavigation();
        _next.Click += async (_, _) => await NextAsync();
        _back.Click += (_, _) => { if (_tabs.SelectedIndex > 0) _tabs.SelectedIndex--; };
        _run.Click += RunButtonClick;
        UpdateNavigation();
    }

    private void BuildPages()
    {
        _tabs.TabPages.Add(new TabPage("1 · Tipo") { Controls = { BuildIntro() } });
        _tabs.TabPages.Add(new TabPage("2 · Origem") { Controls = { BuildSource() } });
        _tabs.TabPages.Add(new TabPage("3 · Lojas") { Controls = { BuildStores() } });
        _tabs.TabPages.Add(new TabPage("4 · Destino") { Controls = { BuildDestination() } });
        _tabs.TabPages.Add(new TabPage("5 · Progresso") { Controls = { _log } });
        _tabs.TabPages.Add(new TabPage("6 · Resultado") { Controls = { new Label { Text = "Pacotes gerados. Consulte o relatório HTML/JSON na pasta escolhida e transfira cada .ffstore com a senha do corte.", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(28) } } });
    }

    private Control BuildIntro()
    {
        var panel = PagePanel();
        panel.Controls.Add(new Label { Text = "Preparar dados para o FarmaFlow local", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 16) });
        panel.Controls.Add(new Label { Text = "O assistente cria um arquivo integral e um pacote criptografado por loja. Nenhuma senha é salva.", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 24) });
        panel.Controls.Add(new Label { Text = "Escolha ensaio para validar uma cópia ou corte definitivo quando o backend cloud já estiver parado.", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 10) });
        panel.Controls.Add(_mode); return panel;
    }

    private Control BuildSource()
    {
        var panel = PagePanel();
        AddField(panel, "Conexão PostgreSQL do Supabase", _connection);
        AddField(panel, "Senha do PostgreSQL", _sourcePassword);
        panel.Controls.Add(new Label { Text = "Use a conexão direta ou o pooler de sessão exibido em Connect. A senha fica somente na memória.", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 12, 0, 0) });
        return panel;
    }

    private Control BuildStores()
    {
        var panel = PagePanel();
        panel.Controls.Add(new Label { Text = "Lojas encontradas", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) });
        panel.Controls.Add(new Label { Text = "Selecione as lojas que receberão um servidor local.", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 12) });
        panel.Controls.Add(_stores); return panel;
    }

    private Control BuildDestination()
    {
        var panel = PagePanel();
        AddField(panel, "Pasta dos pacotes", _output);
        AddField(panel, "Senha única deste corte", _packagePassword);
        panel.Controls.Add(_maintenance);
        panel.Controls.Add(_dataApi);
        AddField(panel, "Chave pública anon (teste do Data API)", _publicApiKey);
        var openDataApi = new Button { Text = "Abrir configuração do Data API", AutoSize = true };
        openDataApi.Click += (_, _) =>
        {
            try
            {
                string host = ParseSource(_connection.Text, string.Empty).Host;
                string projectHost = host.StartsWith("db.", StringComparison.OrdinalIgnoreCase) ? host[3..] : host;
                string projectRef = projectHost.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase)
                    ? projectHost[..^".supabase.co".Length].Split('.')[0]
                    : "_";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://supabase.com/dashboard/project/{projectRef}/settings/api") { UseShellExecute = true });
            }
            catch (Exception exception) { ShowError(exception.Message); }
        };
        panel.Controls.Add(openDataApi);
        return panel;
    }

    private static FlowLayoutPanel PagePanel() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(28), RightToLeft = RightToLeft.No };

    private static void AddField(Control parent, string label, Control field)
    {
        parent.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 0, 4) });
        field.Width = 650; field.Margin = new Padding(0, 0, 0, 8); parent.Controls.Add(field);
    }

    private async Task NextAsync()
    {
        if (_tabs.SelectedIndex == 1)
        {
            try
            {
                MigrationSource source = ParseSource(_connection.Text, _sourcePassword.Text);
                MigrationPipeline.ValidateRuntime(PostgresBin());
                var pipeline = new MigrationPipeline(MigrationExecutable());
                _discoveredStores = await pipeline.DiscoverStoresAsync(source, CancellationToken.None);
                _stores.Items.Clear();
                foreach (StoreChoice store in _discoveredStores) _stores.Items.Add($"{store.Name} · {store.Id}", true);
                Append("Origem validada. Lojas carregadas.");
            }
            catch (Exception exception) { ShowError(exception.Message); return; }
        }
        if (_tabs.SelectedIndex < _tabs.TabPages.Count - 1) _tabs.SelectedIndex++;
    }

    private async Task RunAsync()
    {
        if (_tabs.SelectedIndex != 3) { _tabs.SelectedIndex = 3; return; }
        try
        {
            if (_packagePassword.Text.Length < 12) throw new InvalidOperationException("Escolha uma senha com pelo menos 12 caracteres.");
            var stores = _discoveredStores.Where((_, index) => _stores.GetItemChecked(index)).ToArray();
            if (stores.Length == 0) throw new InvalidOperationException("Selecione pelo menos uma loja.");
            bool final = _mode.SelectedIndex == 1;
            if (final && !_maintenance.Checked) throw new InvalidOperationException("Confirme a manutenção do backend cloud antes do corte.");
            if (final && !_dataApi.Checked) throw new InvalidOperationException("Confirme a proteção do Data API antes do corte.");
            MigrationSource source = ParseSource(_connection.Text, _sourcePassword.Text);
            _tabs.SelectedIndex = 4; _run.Enabled = false; _next.Enabled = false; _back.Enabled = false;
            _cancellation = new CancellationTokenSource();
            var progress = new Progress<OperationProgress>(item => Append($"{item.Percent,3}%  {item.Message}"));
            await new MigrationPipeline(MigrationExecutable()).RunAsync(new MigrationRequest(source, PostgresBin(), _output.Text.Trim(), _packagePassword.Text, stores, final, _maintenance.Checked, _dataApi.Checked, _publicApiKey.Text), progress, _cancellation.Token);
            Append("Pacotes prontos. Transfira cada .ffstore com a mesma senha do corte.");
            _run.Enabled = true;
            _tabs.SelectedIndex = 5;
        }
        catch (Exception exception)
        {
            Append($"ERRO: {exception.Message}");
            ShowError(exception.Message);
            _run.Enabled = true;
            _next.Enabled = true;
            _back.Enabled = true;
            _tabs.SelectedIndex = 3;
        }
        finally { _sourcePassword.Text = string.Empty; _packagePassword.Text = string.Empty; _publicApiKey.Text = string.Empty; }
    }

    private void UpdateNavigation()
    {
        _back.Enabled = _tabs.SelectedIndex > 0 && _tabs.SelectedIndex < 4;
        _next.Visible = _tabs.SelectedIndex is 0 or 1 or 2;
        _run.Visible = _tabs.SelectedIndex is 3 or 5;
        _run.Text = _tabs.SelectedIndex == 3 ? "Gerar pacotes" : "Fechar";
    }

    private async void RunButtonClick(object? sender, EventArgs e)
    {
        if (_tabs.SelectedIndex == 5) { Close(); return; }
        await RunAsync();
    }

    private void Append(string message) => _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    private void ShowError(string message) => MessageBox.Show(this, message, "Não foi possível continuar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private string MigrationExecutable() => Path.Combine(AppContext.BaseDirectory, "FarmaFlow.Migration.exe");
    private string PostgresBin() => Path.Combine(AppContext.BaseDirectory, "postgres", "bin");

    private static MigrationSource ParseSource(string value, string password)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("postgres" or "postgresql"))
            throw new InvalidOperationException("Informe uma conexão PostgreSQL válida.");
        string user = uri.UserInfo.Split(':', 2)[0];
        if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("A conexão precisa informar o usuário PostgreSQL.");
        string database = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(database)) database = "postgres";
        string ssl = QueryValue(uri.Query, "sslmode") ?? "Require";
        return new MigrationSource(uri.Host, uri.Port > 0 ? uri.Port : 5432, database, Uri.UnescapeDataString(user), password, ssl);
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }
}
