using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FarmaFlow.Migration.Core;

/// <summary>
/// Envelopes used by the desktop assistant. Version 1 remains readable so
/// packages created by the original command-line tool can still be restored.
/// Version 2 embeds its manifest and authenticates it as AES-GCM associated data.
/// </summary>
public static class PackageEnvelope
{
    private static readonly byte[] LegacyMagic = "FFMIGR1"u8.ToArray();
    private static readonly byte[] Magic = "FFMIG2"u8.ToArray();
    private const int LegacyVersion = 1;
    private const int Version = 2;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int Iterations = 600_000;

    public sealed record PackageReadResult(
        int FormatVersion,
        JsonDocument? Manifest,
        byte[] Plaintext,
        string PlaintextSha256);

    public static async Task<string> WriteV2Async(
        string outputPath,
        ReadOnlyMemory<byte> plaintext,
        JsonElement manifest,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A senha do pacote é obrigatória.", nameof(password));

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] tag = new byte[TagLength];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        byte[] aad = BuildAssociatedData(manifestBytes);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, aad);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(Magic, cancellationToken);
            await WriteInt32Async(stream, Version, cancellationToken);
            await WriteInt32Async(stream, manifestBytes.Length, cancellationToken);
            await stream.WriteAsync(salt, cancellationToken);
            await stream.WriteAsync(nonce, cancellationToken);
            await stream.WriteAsync(tag, cancellationToken);
            await stream.WriteAsync(manifestBytes, cancellationToken);
            await stream.WriteAsync(ciphertext, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return await Sha256FileAsync(outputPath, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    public static async Task<PackageReadResult> ReadAsync(
        string inputPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        try
        {
            if (payload.AsSpan().StartsWith(Magic))
                return ReadV2(payload, password);
            if (payload.AsSpan().StartsWith(LegacyMagic))
                return ReadV1(payload, password);
            throw new InvalidDataException("O arquivo não é um pacote FarmaFlow válido.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static PackageReadResult ReadV2(byte[] payload, string password)
    {
        int offset = Magic.Length;
        int version = ReadInt32(payload, ref offset);
        if (version != Version) throw new InvalidDataException($"Versão de pacote não suportada: {version}");
        int manifestLength = ReadInt32(payload, ref offset);
        if (manifestLength <= 0 || manifestLength > payload.Length - offset - SaltLength - NonceLength - TagLength)
            throw new InvalidDataException("Manifesto do pacote inválido.");
        byte[] salt = ReadBytes(payload, ref offset, SaltLength);
        byte[] nonce = ReadBytes(payload, ref offset, NonceLength);
        byte[] tag = ReadBytes(payload, ref offset, TagLength);
        byte[] manifestBytes = ReadBytes(payload, ref offset, manifestLength);
        byte[] ciphertext = ReadBytes(payload, ref offset, payload.Length - offset);
        byte[] plaintext = new byte[ciphertext.Length];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        byte[] aad = BuildAssociatedData(manifestBytes);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return new PackageReadResult(Version, JsonDocument.Parse(manifestBytes), plaintext,
                Convert.ToHexString(SHA256.HashData(plaintext)));
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("Senha incorreta ou pacote adulterado.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static PackageReadResult ReadV1(byte[] payload, string password)
    {
        int offset = LegacyMagic.Length;
        int version = ReadInt32(payload, ref offset);
        if (version != LegacyVersion) throw new InvalidDataException($"Versão de pacote não suportada: {version}");
        if (payload.Length < offset + SaltLength + NonceLength + TagLength)
            throw new InvalidDataException("Pacote legado truncado.");
        byte[] salt = ReadBytes(payload, ref offset, SaltLength);
        byte[] nonce = ReadBytes(payload, ref offset, NonceLength);
        byte[] tag = ReadBytes(payload, ref offset, TagLength);
        byte[] ciphertext = ReadBytes(payload, ref offset, payload.Length - offset);
        byte[] plaintext = new byte[ciphertext.Length];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, LegacyMagic);
            return new PackageReadResult(LegacyVersion, null, plaintext,
                Convert.ToHexString(SHA256.HashData(plaintext)));
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("Senha incorreta ou pacote adulterado.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static byte[] BuildAssociatedData(byte[] manifestBytes)
    {
        byte[] result = new byte[Magic.Length + sizeof(int) + manifestBytes.Length];
        Magic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(Magic.Length), Version);
        manifestBytes.CopyTo(result, Magic.Length + sizeof(int));
        return result;
    }

    private static int ReadInt32(byte[] payload, ref int offset)
    {
        if (payload.Length < offset + sizeof(int)) throw new InvalidDataException("Pacote truncado.");
        int value = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static byte[] ReadBytes(byte[] payload, ref int offset, int length)
    {
        if (length < 0 || payload.Length < offset + length) throw new InvalidDataException("Pacote truncado.");
        byte[] result = payload.AsSpan(offset, length).ToArray();
        offset += length;
        return result;
    }

    private static async Task WriteInt32Async(Stream stream, int value, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
