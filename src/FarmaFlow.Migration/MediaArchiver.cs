using Npgsql;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace FarmaFlow.Migration;

internal static class MediaArchiver
{
    private const int MaximumMediaBytes = 20 * 1024 * 1024;

    internal static async Task RunAsync(IReadOnlyDictionary<string, string> values)
    {
        string host = values.GetValueOrDefault("host", "127.0.0.1");
        int port = int.Parse(values.GetValueOrDefault("port", "54329"));
        string database = Required(values, "database");
        string username = values.GetValueOrDefault("username", "farmaflow");
        if (!database.Contains("staging", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mídias só podem ser arquivadas em um banco de staging.");
        string password = ReadSecret("Senha do PostgreSQL de staging: ");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = database, Username = username,
            Password = password, SslMode = SslMode.Prefer, Timeout = 30, CommandTimeout = 0
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await EnsureTableAsync(connection);
        List<MediaReference> media = await ReadMediaAsync(connection);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = ConnectPublicAsync
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FarmaFlow-Migration/1.0");

        int archived = 0;
        int missing = 0;
        foreach (MediaReference reference in media)
        {
            DownloadedMedia downloaded;
            try
            {
                downloaded = await DownloadAsync(http, reference.Url);
            }
            catch (Exception exception)
            {
                await SaveAsync(connection, reference, null, NormalizeMimeType(reference.MimeType), Trim(exception.Message));
                missing++;
                continue;
            }
            await SaveAsync(connection, reference, downloaded.Content, downloaded.MimeType, null);
            archived++;
        }
        password = string.Empty;
        Console.WriteLine($"Mídias arquivadas no banco: {archived}; ausentes: {missing}.");
        if (missing != 0)
            throw new InvalidOperationException(
                $"{missing} mídia(s) não puderam ser arquivadas. Consulte public.local_media_blobs WHERE missing, corrija as URLs ou o acesso à internet e execute archive-media novamente. Mídias já válidas serão preservadas.");
    }

    private static async Task<DownloadedMedia> DownloadAsync(HttpClient http, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"URL de mídia inválida: {url}");
        HttpResponseMessage? response = null;
        try
        {
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                response?.Dispose();
                response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if ((int)response.StatusCode is < 300 or >= 400) break;
                Uri? location = response.Headers.Location;
                if (location is null) throw new InvalidOperationException("Redirecionamento de mídia sem destino.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Redirecionamento de mídia para protocolo não permitido.");
                if (redirects == 5) throw new InvalidOperationException("Mídia excedeu o limite de cinco redirecionamentos.");
            }
            if (response is null) throw new InvalidOperationException("A mídia não produziu resposta HTTP.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumMediaBytes)
                throw new InvalidOperationException("Mídia excede 20 MB.");
            await using Stream input = await response.Content.ReadAsStreamAsync();
            using var output = new MemoryStream();
            byte[] buffer = new byte[81_920];
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                if (output.Length + read > MaximumMediaBytes) throw new InvalidOperationException("Mídia excede 20 MB.");
                await output.WriteAsync(buffer.AsMemory(0, read));
            }
            byte[] content = output.ToArray();
            string? detected = DetectImageMimeType(content);
            if (detected is null)
            {
                CryptographicOperations.ZeroMemory(content);
                throw new InvalidDataException("O conteúdo baixado não é uma imagem raster JPEG, PNG, WebP, GIF ou AVIF válida.");
            }
            string mimeType = detected;
            return new DownloadedMedia(content, mimeType);
        }
        finally { response?.Dispose(); }
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsNonPublic))
            throw new InvalidOperationException($"Destino de mídia privado ou reservado bloqueado: {context.DnsEndPoint.Host}.");
        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception)
            {
                socket.Dispose();
                lastError = exception;
            }
        }
        throw new HttpRequestException($"Não foi possível conectar ao host público {context.DnsEndPoint.Host}.", lastError);
    }

    internal static bool IsNonPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsNonPublic(address.MapToIPv4());
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && (bytes[1] == 0 || bytes[1] == 168))
                || (bytes[0] == 198 && bytes[1] is 18 or 19)
                || bytes[0] >= 224;
        }
        byte[] ipv6 = address.GetAddressBytes();
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || (ipv6[0] & 0xFE) == 0xFC
            || (ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0D && ipv6[3] == 0xB8);
    }

    private static async Task EnsureTableAsync(NpgsqlConnection connection)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS public.local_media_blobs (
                media_id UUID PRIMARY KEY REFERENCES public.product_media(id) ON DELETE CASCADE,
                content BYTEA, mime_type VARCHAR(160), sha256 CHAR(64), source_url TEXT NOT NULL,
                missing BOOLEAN NOT NULL DEFAULT FALSE, failure VARCHAR(500), archived_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                CONSTRAINT ck_local_media_content CHECK ((missing AND content IS NULL) OR (NOT missing AND content IS NOT NULL))
            )
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<MediaReference>> ReadMediaAsync(NpgsqlConnection connection)
    {
        var result = new List<MediaReference>();
        await using var command = new NpgsqlCommand("""
            SELECT media.id,COALESCE(blob.source_url,media.url),media.mime_type
            FROM public.product_media media
            LEFT JOIN public.local_media_blobs blob ON blob.media_id=media.id
            WHERE blob.media_id IS NULL OR blob.missing
            ORDER BY media.id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new MediaReference(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return result;
    }

    private static async Task SaveAsync(NpgsqlConnection connection, MediaReference reference, byte[]? content, string mimeType, string? failure)
    {
        const string sql = """
            INSERT INTO public.local_media_blobs(media_id,content,mime_type,sha256,source_url,missing,failure,archived_at)
            VALUES (@id,@content,@mime,@sha,@url,@missing,@failure,now())
            ON CONFLICT(media_id) DO UPDATE SET content=excluded.content,mime_type=excluded.mime_type,sha256=excluded.sha256,
                source_url=excluded.source_url,missing=excluded.missing,failure=excluded.failure,archived_at=now()
            WHERE public.local_media_blobs.missing;
            UPDATE public.product_media SET url=@local_url,storage_key=@storage_key WHERE id=@id AND @content IS NOT NULL
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", reference.Id);
        command.Parameters.AddWithValue("content", (object?)content ?? DBNull.Value);
        command.Parameters.AddWithValue("mime", mimeType);
        command.Parameters.AddWithValue("sha", content is null ? DBNull.Value : Convert.ToHexString(SHA256.HashData(content)));
        command.Parameters.AddWithValue("url", reference.Url);
        command.Parameters.AddWithValue("missing", content is null);
        command.Parameters.AddWithValue("failure", (object?)failure ?? DBNull.Value);
        command.Parameters.AddWithValue("local_url", $"/backend/public/local-media/{reference.Id}");
        command.Parameters.AddWithValue("storage_key", $"local/{reference.Id}");
        try { await command.ExecuteNonQueryAsync(); }
        finally { if (content is not null) CryptographicOperations.ZeroMemory(content); }
    }

    private static string Trim(string value) => value.Length <= 500 ? value : value[..500];
    internal static string NormalizeMimeType(string? value)
    {
        string normalized = value?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "image/jpeg" or "image/png" or "image/webp" or "image/gif" or "image/avif"
            ? normalized
            : "application/octet-stream";
    }

    internal static string? DetectImageMimeType(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF) return "image/jpeg";
        if (content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (content.Length >= 6 && (content[..6].SequenceEqual("GIF87a"u8) || content[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (content.Length >= 12 && content.Slice(4, 4).SequenceEqual("ftyp"u8)
            && (content.Slice(8, 4).SequenceEqual("avif"u8) || content.Slice(8, 4).SequenceEqual("avis"u8))) return "image/avif";
        return null;
    }
    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"Informe --{name}.");
    private static string ReadSecret(string prompt) => ProcessSecretReader.Read(prompt);

    private sealed record MediaReference(Guid Id, string Url, string? MimeType);
    private sealed record DownloadedMedia(byte[] Content, string MimeType);
}
