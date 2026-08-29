using FarmaFlow.Migration.Core;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit;

namespace FarmaFlow.Agent.Tests;

public sealed class PackageEnvelopeTests
{
    [Fact]
    public async Task LegacyV1RemainsReadableAndRejectsWrongPassword()
    {
        string path = Path.Combine(Path.GetTempPath(), $"farmaflow-{Guid.NewGuid():N}.ffbackup");
        const string password = "senha-legada";
        byte[] plaintext = RandomNumberGenerator.GetBytes(512);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        byte[] magic = "FFMIGR1"u8.ToArray();
        try
        {
            using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plaintext, ciphertext, tag, magic);
            await using (FileStream output = File.Create(path))
            {
                await output.WriteAsync(magic);
                byte[] version = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(version, 1);
                await output.WriteAsync(version);
                await output.WriteAsync(salt);
                await output.WriteAsync(nonce);
                await output.WriteAsync(tag);
                await output.WriteAsync(ciphertext);
            }
            PackageEnvelope.PackageReadResult result = await PackageEnvelope.ReadAsync(path, password);
            Assert.Equal(1, result.FormatVersion);
            Assert.Null(result.Manifest);
            Assert.Equal(plaintext, result.Plaintext);
            CryptographicOperations.ZeroMemory(result.Plaintext);
            await Assert.ThrowsAsync<InvalidDataException>(() => PackageEnvelope.ReadAsync(path, "senha-errada"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task V2RoundTripEmbedsManifestAndDetectsTampering()
    {
        string path = Path.Combine(Path.GetTempPath(), $"farmaflow-{Guid.NewGuid():N}.ffstore");
        try
        {
            using JsonDocument manifest = JsonDocument.Parse("{\"kind\":\"STORE\",\"schemaVersion\":\"52\"}");
            byte[] source = RandomNumberGenerator.GetBytes(4096);
            string hash = await PackageEnvelope.WriteV2Async(path, source, manifest.RootElement, "senha-forte-do-corte");
            Assert.Equal(64, hash.Length);
            PackageEnvelope.PackageReadResult result = await PackageEnvelope.ReadAsync(path, "senha-forte-do-corte");
            Assert.Equal(2, result.FormatVersion);
            Assert.Equal("STORE", result.Manifest!.RootElement.GetProperty("kind").GetString());
            Assert.Equal(source, result.Plaintext);
            CryptographicOperations.ZeroMemory(source);
            CryptographicOperations.ZeroMemory(result.Plaintext);

            byte[] original = await File.ReadAllBytesAsync(path);
            byte[] tampered = original.ToArray();
            tampered[^1] ^= 0x01;
            await File.WriteAllBytesAsync(path, tampered);
            await Assert.ThrowsAsync<InvalidDataException>(() => PackageEnvelope.ReadAsync(path, "senha-forte-do-corte"));
            byte[] manifestTampered = original.ToArray();
            manifestTampered[58] ^= 0x01;
            await File.WriteAllBytesAsync(path, manifestTampered);
            await Assert.ThrowsAsync<InvalidDataException>(() => PackageEnvelope.ReadAsync(path, "senha-forte-do-corte"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task StationBootstrapRoundTripRejectsModifiedEnvelope()
    {
        string path = Path.Combine(Path.GetTempPath(), $"farmaflow-{Guid.NewGuid():N}.ffstation");
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FarmaFlow test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
        string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        try
        {
            var info = new StationBootstrapInfo("https://server:8443", "SERVER", "store", "Loja", fingerprint, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
            await StationBootstrapPackage.WriteAsync(path, info, certificate);
            StationBootstrapInfo read = StationBootstrapPackage.ReadAndValidate(path);
            Assert.Equal(info.ServerUrl, read.ServerUrl);
            Assert.Equal(info.StoreName, read.StoreName);
            string json = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, json.Replace("\"signature\": \"", "\"signature\": \"x", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => StationBootstrapPackage.ReadAndValidate(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
