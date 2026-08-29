using FarmaFlow.Agent.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FarmaFlow.Agent;

public sealed class DesktopWindow : Form
{
    private readonly DesktopConnectionStore _connections;
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };

    public DesktopWindow(DesktopConnectionStore connections)
    {
        _connections = connections;
        Text = "FarmaFlow";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1024, 700);
        Icon = TrayApplicationContext.LoadTrayIcon();
        Controls.Add(_browser);
        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason != CloseReason.WindowsShutDown)
            {
                eventArgs.Cancel = true;
                Hide();
            }
        };
    }

    public async Task NavigateAsync()
    {
        DesktopConnection connection = _connections.Load();
        if (string.IsNullOrWhiteSpace(connection.CertificateSha256))
        {
            using var dialog = new ServerConnectionDialog(connection);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Connection is null) return;
            _connections.Save(dialog.Connection);
            connection = dialog.Connection;
        }

        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FarmaFlow", "Agent", "WebView2");
        Show();
        Activate();
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await _browser.EnsureCoreWebView2Async(environment);
        _browser.CoreWebView2.ServerCertificateErrorDetected -= ValidateCertificate;
        _browser.CoreWebView2.ServerCertificateErrorDetected += ValidateCertificate;
        _browser.Source = new Uri(connection.ServerUrl);
    }

    public void Configure()
    {
        using var dialog = new ServerConnectionDialog(_connections.Load());
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Connection is null) return;
        _connections.Save(dialog.Connection);
        _ = NavigateAsync();
    }

    private void ValidateCertificate(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs eventArgs)
    {
        try
        {
            DesktopConnection connection = _connections.Load();
            if (!Uri.TryCreate(eventArgs.RequestUri, UriKind.Absolute, out Uri? request)
                || !Uri.TryCreate(connection.ServerUrl, UriKind.Absolute, out Uri? configured)
                || !string.Equals(request.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
                || request.Port != configured.Port)
            {
                eventArgs.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                return;
            }

            using X509Certificate2 certificate = eventArgs.ServerCertificate.ToX509Certificate2();
            string actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            eventArgs.Action = CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(connection.CertificateSha256))
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Cancel;
        }
        catch
        {
            eventArgs.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        }
    }
}
