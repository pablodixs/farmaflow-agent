using FarmaFlow.Agent.Infrastructure;

namespace FarmaFlow.Agent;

public sealed class ServerConnectionDialog : Form
{
    private readonly TextBox _serverUrl = new() { Dock = DockStyle.Fill };
    private readonly TextBox _fingerprint = new() { Dock = DockStyle.Fill, CharacterCasing = CharacterCasing.Upper };

    public DesktopConnection? Connection { get; private set; }

    public ServerConnectionDialog(DesktopConnection current)
    {
        Text = "Configuração avançada — servidor FarmaFlow";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(620, 190);
        Icon = TrayApplicationContext.LoadTrayIcon();

        _serverUrl.Text = current.ServerUrl;
        _fingerprint.Text = current.CertificateSha256;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Endereço HTTPS do servidor", AutoSize = true });
        layout.Controls.Add(_serverUrl);
        layout.Controls.Add(new Label { Text = "Impressão digital SHA-256 do certificado", AutoSize = true });
        layout.Controls.Add(_fingerprint);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var save = new Button { Text = "Salvar", AutoSize = true };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveConnection();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(new Label
        {
            Text = "Compare a impressão digital com a exibida no servidor antes de salvar.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 8)
        });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void SaveConnection()
    {
        try
        {
            string url = _serverUrl.Text.Trim().TrimEnd('/');
            string fingerprint = DesktopConnectionStore.NormalizeFingerprint(_fingerprint.Text);
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Informe uma URL HTTPS válida.");
            if (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidOperationException("Informe os 64 caracteres da impressão digital SHA-256.");
            Connection = new DesktopConnection(url, fingerprint);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Configuração inválida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
