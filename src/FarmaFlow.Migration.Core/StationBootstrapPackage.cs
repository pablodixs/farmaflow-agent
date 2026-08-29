using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace FarmaFlow.Migration.Core;

public sealed record StationBootstrapInfo(
    string ServerUrl,
    string ServerName,
    string StoreId,
    string StoreName,
    string CertificateSha256,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public static class StationBootstrapPackage
{
    private const string Format = "FarmaFlow station bootstrap";
    private const int Version = 1;

    public static async Task WriteAsync(string path, StationBootstrapInfo info, X509Certificate2 certificate, CancellationToken cancellationToken = default)
    {
        using RSA key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("O certificado do servidor não possui chave privada RSA.");
        string certificateSha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        if (!string.Equals(certificateSha256, info.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A impressão digital do certificado não corresponde ao pacote da estação.");
        var payload = new
        {
            format = Format,
            version = Version,
            serverUrl = info.ServerUrl.TrimEnd('/'),
            serverName = info.ServerName,
            storeId = info.StoreId,
            storeName = info.StoreName,
            certificateDer = Convert.ToBase64String(certificate.RawData),
            certificateSha256,
            issuedAt = info.IssuedAt,
            expiresAt = info.ExpiresAt
        };
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] signature = key.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var envelope = new { format = Format, version = Version, payload = Base64Url(payloadBytes), signature = Base64Url(signature) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, cancellationToken);
        CryptographicOperations.ZeroMemory(payloadBytes);
        CryptographicOperations.ZeroMemory(signature);
    }

    public static StationBootstrapInfo ReadAndValidate(string path)
    {
        using JsonDocument envelope = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = envelope.RootElement;
        if (root.GetProperty("format").GetString() != Format || root.GetProperty("version").GetInt32() != Version)
            throw new InvalidDataException("Arquivo de configuração de estação incompatível.");
        byte[] payloadBytes = FromBase64Url(root.GetProperty("payload").GetString() ?? string.Empty);
        byte[] signature = FromBase64Url(root.GetProperty("signature").GetString() ?? string.Empty);
        try
        {
            using JsonDocument payload = JsonDocument.Parse(payloadBytes);
            JsonElement value = payload.RootElement;
            byte[] certificateRaw = Convert.FromBase64String(value.GetProperty("certificateDer").GetString() ?? string.Empty);
            using var certificate = new X509Certificate2(certificateRaw);
            using RSA key = certificate.GetRSAPublicKey() ?? throw new InvalidDataException("Certificado da estação inválido.");
            if (!key.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidDataException("A assinatura do arquivo de estação é inválida.");
            string fingerprint = Convert.ToHexString(SHA256.HashData(certificateRaw));
            if (!string.Equals(fingerprint, value.GetProperty("certificateSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O certificado do arquivo de estação foi alterado.");
            DateTimeOffset issuedAt = value.GetProperty("issuedAt").GetDateTimeOffset();
            DateTimeOffset expiresAt = value.GetProperty("expiresAt").GetDateTimeOffset();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (issuedAt > now.AddMinutes(5) || expiresAt <= now)
                throw new InvalidDataException("O arquivo de estação está fora da validade. Gere uma configuração nova no servidor.");
            if (expiresAt - issuedAt > TimeSpan.FromDays(30).Add(TimeSpan.FromMinutes(1)))
                throw new InvalidDataException("A validade do arquivo de estação excede o limite de 30 dias.");
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                throw new InvalidDataException("O certificado do servidor está fora da validade.");
            string serverUrl = value.GetProperty("serverUrl").GetString() ?? string.Empty;
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("O arquivo de estação não contém uma URL HTTPS válida.");
            return new StationBootstrapInfo(
                serverUrl,
                value.GetProperty("serverName").GetString() ?? string.Empty,
                value.GetProperty("storeId").GetString() ?? string.Empty,
                value.GetProperty("storeName").GetString() ?? string.Empty,
                fingerprint,
                issuedAt, expiresAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}
