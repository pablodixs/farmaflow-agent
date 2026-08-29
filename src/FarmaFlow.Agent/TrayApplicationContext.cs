using FarmaFlow.Agent.Infrastructure;
using FarmaFlow.Agent.Services;

namespace FarmaFlow.Agent;

public sealed class TrayApplicationContext : ApplicationContext
{
    private const string TrayIconResource = "FarmaFlow.Agent.Assets.farmaflow.ico";
    private readonly NotifyIcon _tray;
    private readonly DesktopWindow _window;

    public TrayApplicationContext(DesktopConnectionStore connections, PairingService pairing, AgentStore store)
    {
        _window = new DesktopWindow(connections);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir FarmaFlow", null, async (_, _) => await _window.NavigateAsync());
        menu.Items.Add("Configuração avançada", null, (_, _) => _window.Configure());
        menu.Items.Add("Parear estação", null, async (_, _) => await Pair(pairing));
        menu.Items.Add("Status", null, (_, _) => MessageBox.Show(
            store.GetRegistration() is null ? "Agente não pareado." : $"Agente conectado.\nPendências: {store.PendingCount()}",
            "FarmaFlow Agent"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());
        _tray = new NotifyIcon
        {
            Text = "FarmaFlow Agent",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += async (_, _) => await _window.NavigateAsync();
    }

    protected override void ExitThreadCore() { _tray.Visible = false; _tray.Dispose(); _window.Dispose(); base.ExitThreadCore(); }

    internal static Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream(TrayIconResource);
            if (stream is not null)
            {
                using var icon = new Icon(stream);
                return (Icon)icon.Clone();
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write(exception, "Não foi possível carregar o ícone incorporado.");
        }

        return SystemIcons.Application;
    }

    private static async Task Pair(PairingService pairing)
    {
        var code = Microsoft.VisualBasic.Interaction.InputBox("Informe o código exibido no FarmaFlow:", "Parear estação");
        if (string.IsNullOrWhiteSpace(code)) return;
        try { await pairing.PairAsync(code); MessageBox.Show("Estação conectada com sucesso.", "FarmaFlow Agent"); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Falha no pareamento", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
