namespace FarmaFlow.Migration.Core;

public sealed record SupabaseProjectAddress(string ProjectRef, Uri BaseUri)
{
    public static SupabaseProjectAddress Resolve(string databaseHost, string databaseUsername, string? explicitProjectUrl = null)
    {
        string? projectRef = ProjectRefFromDatabaseHost(databaseHost)
            ?? ProjectRefFromPoolerUsername(databaseUsername);

        if (!string.IsNullOrWhiteSpace(explicitProjectUrl))
        {
            string value = explicitProjectUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? explicitUri)
                || explicitUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(explicitUri.PathAndQuery.Trim('/')))
                throw new InvalidOperationException("Informe a URL HTTPS raiz do projeto Supabase, sem caminhos adicionais.");

            projectRef ??= ProjectRefFromProjectHost(explicitUri.Host);
            if (string.IsNullOrWhiteSpace(projectRef))
                throw new InvalidOperationException("Não foi possível identificar o project ref. Informe uma URL no formato https://<project-ref>.supabase.co.");
            return new SupabaseProjectAddress(projectRef, explicitUri);
        }

        if (string.IsNullOrWhiteSpace(projectRef))
            throw new InvalidOperationException("Não foi possível identificar o projeto pelo host/usuário do pooler. Informe também a URL do projeto Supabase.");

        return new SupabaseProjectAddress(projectRef, new Uri($"https://{projectRef}.supabase.co"));
    }

    private static string? ProjectRefFromDatabaseHost(string host)
    {
        string normalized = host.Trim().TrimEnd('.');
        if (!normalized.StartsWith("db.", StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase)) return null;
        string candidate = normalized[3..^".supabase.co".Length];
        return IsProjectRef(candidate) ? candidate : null;
    }

    private static string? ProjectRefFromPoolerUsername(string username)
    {
        string normalized = username.Trim();
        int separator = normalized.LastIndexOf('.');
        if (separator < 0 || separator == normalized.Length - 1) return null;
        string candidate = normalized[(separator + 1)..];
        return IsProjectRef(candidate) ? candidate : null;
    }

    private static string? ProjectRefFromProjectHost(string host)
    {
        string normalized = host.Trim().TrimEnd('.');
        if (!normalized.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase)) return null;
        string candidate = normalized[..^".supabase.co".Length];
        return !candidate.Contains('.') && IsProjectRef(candidate) ? candidate : null;
    }

    private static bool IsProjectRef(string value) =>
        value.Length is >= 8 and <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
