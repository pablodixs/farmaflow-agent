using System.Text.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FarmaFlow.Migration.Core;

namespace FarmaFlow.Agent.Infrastructure;

public sealed record DesktopConnection(string ServerUrl, string CertificateSha256)
{
    public string BackendUrl => $"{ServerUrl.TrimEnd('/')}/backend";
}

public sealed class DesktopConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly AgentOptions _options;

    public DesktopConnectionStore(AgentOptions options)
    {
        _options = options;
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FarmaFlow", "Agent");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "server.json");
    }

    public DesktopConnection Load()
    {
        if (!File.Exists(_path)) return new DesktopConnection(_options.WebAppUrl, string.Empty);
        return JsonSerializer.Deserialize<DesktopConnection>(File.ReadAllText(_path))
            ?? new DesktopConnection(_options.WebAppUrl, string.Empty);
    }

    public void Save(DesktopConnection connection)
    {
        string url = connection.ServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Informe uma URL HTTPS válida para o servidor FarmaFlow.");

        string fingerprint = NormalizeFingerprint(connection.CertificateSha256);
        if (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("A impressão digital deve conter 64 caracteres SHA-256.");

        File.WriteAllText(_path, JsonSerializer.Serialize(new DesktopConnection(url, fingerprint), JsonOptions));
    }

    public StationBootstrapInfo ImportStationPackage(string path)
    {
        StationBootstrapInfo info = StationBootstrapPackage.ReadAndValidate(path);
        Save(new DesktopConnection(info.ServerUrl, info.CertificateSha256));
        return info;
    }

    public bool IsAllowedOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? candidate)) return false;
        if (!Uri.TryCreate(Load().ServerUrl, UriKind.Absolute, out Uri? configured)) return false;
        return string.Equals(candidate.Scheme, configured.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
            && candidate.Port == configured.Port;
    }

    public bool IsValidCertificate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? _, SslPolicyErrors errors)
    {
        if (certificate is null || request.RequestUri is null) return false;
        DesktopConnection connection = Load();
        if (!Uri.TryCreate(connection.ServerUrl, UriKind.Absolute, out Uri? configured)
            || !string.Equals(request.RequestUri.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
            || request.RequestUri.Port != configured.Port)
            return errors == SslPolicyErrors.None;
        string expected = NormalizeFingerprint(connection.CertificateSha256);
        if (expected.Length != 64) return false;
        string actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
    }

    public static string NormalizeFingerprint(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}
