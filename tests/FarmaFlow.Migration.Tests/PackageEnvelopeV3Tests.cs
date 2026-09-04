using FarmaFlow.Migration.Core;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace FarmaFlow.Migration.Tests;

public sealed class PackageEnvelopeV3Tests
{
    [Fact]
    public async Task StreamsMultipleChunksAndDoesNotPublishPartialPlaintext()
    {
        string root = Path.Combine(Path.GetTempPath(), $"farmaflow-envelope-{Guid.NewGuid():N}");
        string sourcePath = Path.Combine(root, "source.dump");
        string packagePath = Path.Combine(root, "store.ffstore");
        string restoredPath = Path.Combine(root, "restored.dump");
        Directory.CreateDirectory(root);
        try
        {
            byte[] source = RandomNumberGenerator.GetBytes(4 * 1024 * 1024 + 37);
            await File.WriteAllBytesAsync(sourcePath, source);
            string plaintextHash = Convert.ToHexString(SHA256.HashData(source));
            using JsonDocument manifest = JsonDocument.Parse($"{{\"kind\":\"STORE\",\"schemaVersion\":\"54\",\"plaintextSha256\":\"{plaintextHash}\"}}");

            string packageHash = await PackageEnvelope.WriteV3Async(packagePath, sourcePath, manifest.RootElement, "senha-forte-do-corte");
            Assert.Equal(64, packageHash.Length);

            PackageEnvelope.PackageExtractResult restored = await PackageEnvelope.ExtractAsync(packagePath, restoredPath, "senha-forte-do-corte");
            Assert.Equal(3, restored.FormatVersion);
            Assert.Equal(source.LongLength, restored.PlaintextLength);
            Assert.Equal(plaintextHash, restored.PlaintextSha256);
            Assert.Equal(source, await File.ReadAllBytesAsync(restoredPath));
            restored.Manifest?.Dispose();

            byte[] tampered = await File.ReadAllBytesAsync(packagePath);
            tampered[^17] ^= 0x01;
            await File.WriteAllBytesAsync(packagePath, tampered);
            File.Delete(restoredPath);
            await Assert.ThrowsAsync<InvalidDataException>(() => PackageEnvelope.ExtractAsync(packagePath, restoredPath, "senha-forte-do-corte"));
            Assert.False(File.Exists(restoredPath));
            CryptographicOperations.ZeroMemory(source);
            CryptographicOperations.ZeroMemory(tampered);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
