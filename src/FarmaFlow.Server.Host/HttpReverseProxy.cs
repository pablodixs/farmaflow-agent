using System.Net.Http.Headers;

namespace FarmaFlow.Server.Host;

public static class HttpReverseProxy
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host"
    };

    public static async Task ForwardAsync(
        HttpContext context,
        HttpClient client,
        Uri target,
        bool trustedSameOrigin,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(context.Request.Body);

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            if (trustedSameOrigin && header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString());
        if (trustedSameOrigin) request.Headers.TryAddWithoutValidation("X-FarmaFlow-Local-Proxy", "1");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        context.Response.StatusCode = (int)response.StatusCode;
        CopyHeaders(response.Headers, context.Response.Headers);
        CopyHeaders(response.Content.Headers, context.Response.Headers);
        context.Response.Headers.Remove("transfer-encoding");
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static void CopyHeaders(HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            if (!HopByHopHeaders.Contains(header.Key)) destination[header.Key] = header.Value.ToArray();
        }
    }
}
