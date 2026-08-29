using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FarmaFlow.Server.Host;

public static class LocalCertificateProvider
{
    private const string FriendlyName = "FarmaFlow Local Server";

    public static X509Certificate2 LoadOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        X509Certificate2? existing = store.Certificates
            .Find(X509FindType.FindBySubjectName, Environment.MachineName, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(c => c.FriendlyName == FriendlyName && c.NotAfter > DateTimeOffset.UtcNow.AddDays(30));
        if (existing is not null)
        {
            WriteFingerprint(existing);
            return existing;
        }

        using RSA rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN={Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")],
            true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (IPAddress address in GetPrivateAddresses()) san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(2));
        var persisted = new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        persisted.FriendlyName = FriendlyName;
        store.Add(persisted);
        WriteFingerprint(persisted);
        return persisted;
    }

    public static string Sha256(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static void WriteFingerprint(X509Certificate2 certificate)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "FarmaFlow", "Server");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "certificate.sha256.txt"),
            $"Servidor: {Environment.MachineName}{Environment.NewLine}SHA-256: {Sha256(certificate)}{Environment.NewLine}");
    }

    private static IEnumerable<IPAddress> GetPrivateAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
}
