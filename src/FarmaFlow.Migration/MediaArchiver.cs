using Npgsql;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

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
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FarmaFlow-Migration/1.0");

        int archived = 0;
        int missing = 0;
        foreach (MediaReference reference in media)
        {
            try
            {
                byte[] content = await DownloadAsync(http, reference.Url);
                string mimeType = string.IsNullOrWhiteSpace(reference.MimeType) ? "application/octet-stream" : reference.MimeType;
                await SaveAsync(connection, reference, content, mimeType, null);
                archived++;
            }
            catch (Exception exception)
            {
                await SaveAsync(connection, reference, null, reference.MimeType ?? "application/octet-stream", Trim(exception.Message));
                missing++;
            }
        }
        password = string.Empty;
        Console.WriteLine($"Mídias arquivadas no banco: {archived}; ausentes: {missing}.");
    }

    private static async Task<byte[]> DownloadAsync(HttpClient http, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("URL de mídia inválida.");
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.Host);
        if (addresses.Length == 0 || addresses.Any(IsPrivate)) throw new InvalidOperationException("Destino de mídia privado bloqueado.");
        using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
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
        return output.ToArray();
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Loopback);
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
        await using var command = new NpgsqlCommand("SELECT id,url,mime_type FROM public.product_media ORDER BY id", connection);
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
                source_url=excluded.source_url,missing=excluded.missing,failure=excluded.failure,archived_at=now();
            UPDATE public.product_media SET url=@local_url,storage_key=@storage_key WHERE id=@id
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
        await command.ExecuteNonQueryAsync();
        if (content is not null) CryptographicOperations.ZeroMemory(content);
    }

    private static string Trim(string value) => value.Length <= 500 ? value : value[..500];
    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"Informe --{name}.");
    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var result = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && result.Length > 0) result.Length--;
            else if (!char.IsControl(key.KeyChar)) result.Append(key.KeyChar);
        }
        Console.WriteLine();
        return result.ToString();
    }

    private sealed record MediaReference(Guid Id, string Url, string? MimeType);
}
