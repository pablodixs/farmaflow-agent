using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FarmaFlow.Migration.Core;

/// <summary>
/// Envelopes used by the desktop assistant. Version 1 remains readable so
/// packages created by the original command-line tool can still be restored.
/// Version 2 embeds its manifest and authenticates it as AES-GCM associated data.
/// Version 3 encrypts authenticated chunks so production-sized dumps never need
/// to be materialized completely in memory.
/// </summary>
public static class PackageEnvelope
{
    private static readonly byte[] LegacyMagic = "FFMIGR1"u8.ToArray();
    private static readonly byte[] V2Magic = "FFMIG2"u8.ToArray();
    private static readonly byte[] Magic = "FFMIG3"u8.ToArray();
    private const int LegacyVersion = 1;
    private const int V2Version = 2;
    private const int Version = 3;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int NoncePrefixLength = 8;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int Iterations = 600_000;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int MaximumManifestLength = 16 * 1024 * 1024;

    public sealed record PackageReadResult(
        int FormatVersion,
        JsonDocument? Manifest,
        byte[] Plaintext,
        string PlaintextSha256);

    public sealed record PackageExtractResult(
        int FormatVersion,
        JsonDocument? Manifest,
        long PlaintextLength,
        string PlaintextSha256);

    public static async Task<string> WriteV3Async(
        string outputPath,
        string plaintextPath,
        JsonElement manifest,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateNewPassword(password);

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (manifestBytes.Length is <= 0 or > MaximumManifestLength)
            throw new InvalidDataException("Manifesto do pacote inválido.");

        long plaintextLength = new FileInfo(plaintextPath).Length;
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixLength);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        string temporary = $"{fullOutput}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using FileStream input = new(plaintextPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await output.WriteAsync(Magic, cancellationToken);
            await WriteInt32Async(output, Version, cancellationToken);
            await WriteInt32Async(output, manifestBytes.Length, cancellationToken);
            await WriteInt32Async(output, ChunkSize, cancellationToken);
            await WriteInt64Async(output, plaintextLength, cancellationToken);
            await output.WriteAsync(salt, cancellationToken);
            await output.WriteAsync(noncePrefix, cancellationToken);
            await output.WriteAsync(manifestBytes, cancellationToken);

            byte[] plaintext = new byte[ChunkSize];
            byte[] ciphertext = new byte[ChunkSize];
            byte[] tag = new byte[TagLength];
            try
            {
                using var aes = new AesGcm(key, TagLength);
                long remaining = plaintextLength;
                long chunkIndex = 0;
                do
                {
                    int expected = (int)Math.Min(ChunkSize, remaining);
                    await ReadExactlyAsync(input, plaintext.AsMemory(0, expected), cancellationToken);
                    byte[] nonce = BuildChunkNonce(noncePrefix, chunkIndex);
                    byte[] aad = BuildV3AssociatedData(manifestBytes, plaintextLength, ChunkSize, chunkIndex);
                    try
                    {
                        aes.Encrypt(nonce, plaintext.AsSpan(0, expected), ciphertext.AsSpan(0, expected), tag, aad);
                        await output.WriteAsync(ciphertext.AsMemory(0, expected), cancellationToken);
                        await output.WriteAsync(tag, cancellationToken);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(nonce);
                        CryptographicOperations.ZeroMemory(aad);
                    }
                    remaining -= expected;
                    chunkIndex++;
                } while (remaining > 0 || chunkIndex == 0);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
            }
            await output.FlushAsync(cancellationToken);
            output.Close();
            File.Move(temporary, fullOutput, overwrite: true);
            return await Sha256FileAsync(fullOutput, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(noncePrefix);
            CryptographicOperations.ZeroMemory(manifestBytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<PackageExtractResult> ExtractAsync(
        string inputPath,
        string? outputPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        await using FileStream input = new(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] prefix = new byte[Math.Max(Magic.Length, LegacyMagic.Length)];
        int prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            int read = await input.ReadAsync(prefix.AsMemory(prefixLength), cancellationToken);
            if (read == 0) break;
            prefixLength += read;
        }
        input.Position = 0;
        if (prefix.AsSpan(0, prefixLength).StartsWith(Magic))
            return await ExtractV3Async(input, outputPath, password, cancellationToken);

        // V1/V2 remain supported for old packages. Their original one-shot GCM
        // layout cannot be decrypted incrementally, so only the legacy path uses memory.
        PackageReadResult legacy = await ReadAsync(inputPath, password, cancellationToken);
        try
        {
            if (outputPath is not null) await WriteAtomicallyAsync(outputPath, legacy.Plaintext, cancellationToken);
            return new PackageExtractResult(legacy.FormatVersion, legacy.Manifest, legacy.Plaintext.Length, legacy.PlaintextSha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy.Plaintext);
        }
    }

    public static async Task<string> WriteV2Async(
        string outputPath,
        ReadOnlyMemory<byte> plaintext,
        JsonElement manifest,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidateNewPassword(password);

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] tag = new byte[TagLength];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        byte[] aad = BuildV2AssociatedData(manifestBytes);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, aad);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(V2Magic, cancellationToken);
                await WriteInt32Async(stream, V2Version, cancellationToken);
                await WriteInt32Async(stream, manifestBytes.Length, cancellationToken);
                await stream.WriteAsync(salt, cancellationToken);
                await stream.WriteAsync(nonce, cancellationToken);
                await stream.WriteAsync(tag, cancellationToken);
                await stream.WriteAsync(manifestBytes, cancellationToken);
                await stream.WriteAsync(ciphertext, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
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
            CryptographicOperations.ZeroMemory(manifestBytes);
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
                return ReadV3(payload, password);
            if (payload.AsSpan().StartsWith(V2Magic))
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
        int offset = V2Magic.Length;
        int version = ReadInt32(payload, ref offset);
        if (version != V2Version) throw new InvalidDataException($"Versão de pacote não suportada: {version}");
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
        byte[] aad = BuildV2AssociatedData(manifestBytes);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return new PackageReadResult(V2Version, JsonDocument.Parse(Encoding.UTF8.GetString(manifestBytes)), plaintext,
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
            CryptographicOperations.ZeroMemory(manifestBytes);
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

    private static PackageReadResult ReadV3(byte[] payload, string password)
    {
        string temporary = Path.Combine(Path.GetTempPath(), $"farmaflow-package-read-{Guid.NewGuid():N}.tmp");
        try
        {
            PackageExtractResult result = ExtractAsyncFromMemory(payload, temporary, password);
            byte[] plaintext = File.ReadAllBytes(temporary);
            return new PackageReadResult(result.FormatVersion, result.Manifest, plaintext, result.PlaintextSha256);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static PackageExtractResult ExtractAsyncFromMemory(byte[] payload, string outputPath, string password)
    {
        using var input = new MemoryStream(payload, writable: false);
        return ExtractV3Async(input, outputPath, password, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<PackageExtractResult> ExtractV3Async(
        Stream input,
        string? outputPath,
        string password,
        CancellationToken cancellationToken)
    {
        byte[] magic = new byte[Magic.Length];
        await ReadExactlyAsync(input, magic, cancellationToken);
        int version = await ReadInt32Async(input, cancellationToken);
        if (version != Version) throw new InvalidDataException($"Versão de pacote não suportada: {version}");
        int manifestLength = await ReadInt32Async(input, cancellationToken);
        int chunkSize = await ReadInt32Async(input, cancellationToken);
        long plaintextLength = await ReadInt64Async(input, cancellationToken);
        if (manifestLength is <= 0 or > MaximumManifestLength || chunkSize is < 65_536 or > 16 * 1024 * 1024 || plaintextLength < 0)
            throw new InvalidDataException("Cabeçalho do pacote inválido.");
        long chunks;
        long expectedLength;
        try
        {
            chunks = Math.Max(1, checked((plaintextLength + chunkSize - 1) / chunkSize));
            if (chunks - 1 > uint.MaxValue)
                throw new InvalidDataException("Pacote excede o limite de blocos suportado.");
            expectedLength = checked(Magic.Length + sizeof(int) * 3L + sizeof(long) + SaltLength + NoncePrefixLength + manifestLength + plaintextLength + chunks * TagLength);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("O tamanho declarado no pacote é inválido.", exception);
        }
        if (input.CanSeek && input.Length != expectedLength)
            throw new InvalidDataException("Pacote truncado ou com dados excedentes.");

        byte[] salt = new byte[SaltLength];
        byte[] noncePrefix = new byte[NoncePrefixLength];
        byte[] manifestBytes = new byte[manifestLength];
        await ReadExactlyAsync(input, salt, cancellationToken);
        await ReadExactlyAsync(input, noncePrefix, cancellationToken);
        await ReadExactlyAsync(input, manifestBytes, cancellationToken);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        if (outputPath is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        string? temporary = outputPath is null ? null : $"{Path.GetFullPath(outputPath)}.{Guid.NewGuid():N}.tmp";
        Stream output = temporary is null
            ? Stream.Null
            : new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, chunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] ciphertext = new byte[chunkSize];
        byte[] plaintext = new byte[chunkSize];
        byte[] tag = new byte[TagLength];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            long remaining = plaintextLength;
            for (long chunkIndex = 0; chunkIndex < chunks; chunkIndex++)
            {
                int length = (int)Math.Min(chunkSize, remaining);
                await ReadExactlyAsync(input, ciphertext.AsMemory(0, length), cancellationToken);
                await ReadExactlyAsync(input, tag, cancellationToken);
                byte[] nonce = BuildChunkNonce(noncePrefix, chunkIndex);
                byte[] aad = BuildV3AssociatedData(manifestBytes, plaintextLength, chunkSize, chunkIndex);
                try
                {
                    aes.Decrypt(nonce, ciphertext.AsSpan(0, length), tag, plaintext.AsSpan(0, length), aad);
                }
                catch (CryptographicException exception)
                {
                    throw new InvalidDataException("Senha incorreta ou pacote adulterado.", exception);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(aad);
                }
                hash.AppendData(plaintext, 0, length);
                await output.WriteAsync(plaintext.AsMemory(0, length), cancellationToken);
                remaining -= length;
            }
            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
            output = Stream.Null;
            string plaintextSha256 = Convert.ToHexString(hash.GetHashAndReset());
            JsonDocument manifest = JsonDocument.Parse(Encoding.UTF8.GetString(manifestBytes));
            if (manifest.RootElement.TryGetProperty("plaintextSha256", out JsonElement expectedHash)
                && !string.Equals(expectedHash.GetString(), plaintextSha256, StringComparison.OrdinalIgnoreCase))
            {
                manifest.Dispose();
                throw new InvalidDataException("O conteúdo descriptografado não corresponde ao manifesto autenticado.");
            }
            if (temporary is not null)
            {
                File.Move(temporary, Path.GetFullPath(outputPath!), overwrite: true);
                temporary = null;
            }
            return new PackageExtractResult(Version, manifest, plaintextLength, plaintextSha256);
        }
        finally
        {
            await output.DisposeAsync();
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(noncePrefix);
            CryptographicOperations.ZeroMemory(manifestBytes);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(tag);
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] BuildV2AssociatedData(byte[] manifestBytes)
    {
        byte[] result = new byte[V2Magic.Length + sizeof(int) + manifestBytes.Length];
        V2Magic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(V2Magic.Length), V2Version);
        manifestBytes.CopyTo(result, V2Magic.Length + sizeof(int));
        return result;
    }

    private static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A senha do pacote é obrigatória.", nameof(password));
        if (password.Length < 12)
            throw new ArgumentException("A senha de um novo pacote precisa ter pelo menos 12 caracteres.", nameof(password));
    }

    private static byte[] BuildV3AssociatedData(byte[] manifestBytes, long plaintextLength, int chunkSize, long chunkIndex)
    {
        byte[] manifestHash = SHA256.HashData(manifestBytes);
        byte[] result = new byte[Magic.Length + sizeof(int) * 2 + sizeof(long) * 2 + manifestHash.Length];
        int offset = 0;
        Magic.CopyTo(result, offset); offset += Magic.Length;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), Version); offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), chunkSize); offset += sizeof(int);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset), plaintextLength); offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(offset), chunkIndex); offset += sizeof(long);
        manifestHash.CopyTo(result, offset);
        CryptographicOperations.ZeroMemory(manifestHash);
        return result;
    }

    private static byte[] BuildChunkNonce(byte[] prefix, long chunkIndex)
    {
        if (chunkIndex > uint.MaxValue) throw new InvalidDataException("Pacote excede o limite de blocos suportado.");
        byte[] nonce = new byte[NonceLength];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLength), (uint)chunkIndex);
        return nonce;
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await input.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new InvalidDataException("Pacote truncado.");
            offset += read;
        }
    }

    private static async Task WriteAtomicallyAsync(string outputPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        string temporary = $"{fullOutput}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(content, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, fullOutput, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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

    private static async Task WriteInt64Async(Stream stream, long value, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, bytes, cancellationToken);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static async Task<long> ReadInt64Async(Stream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[sizeof(long)];
        await ReadExactlyAsync(stream, bytes, cancellationToken);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }
}
