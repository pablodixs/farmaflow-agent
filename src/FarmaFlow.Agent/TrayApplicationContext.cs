using FarmaFlow.Agent.Infrastructure;
using FarmaFlow.Agent.Services;

namespace FarmaFlow.Agent;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;

    public TrayApplicationContext(AgentOptions options, PairingService pairing, AgentStore store)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir FarmaFlow", null, (_, _) => Open(options.WebAppUrl));
        menu.Items.Add("Parear estação", null, async (_, _) => await Pair(pairing));
        menu.Items.Add("Status", null, (_, _) => MessageBox.Show(
            store.GetRegistration() is null ? "Agente não pareado." : $"Agente conectado.\nPendências: {store.PendingCount()}",
            "FarmaFlow Agent"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());
        _tray = new NotifyIcon
        {
            Text = "FarmaFlow Agent",
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "farmaflow.ico")),
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Open(options.WebAppUrl);
    }

    protected override void ExitThreadCore() { _tray.Visible = false; _tray.Dispose(); base.ExitThreadCore(); }

    private static void Open(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private static async Task Pair(PairingService pairing)
    {
        var code = Microsoft.VisualBasic.Interaction.InputBox("Informe o código exibido no FarmaFlow:", "Parear estação");
        if (string.IsNullOrWhiteSpace(code)) return;
        try { await pairing.PairAsync(code); MessageBox.Show("Estação pareada com sucesso.", "FarmaFlow Agent"); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Falha no pareamento", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
