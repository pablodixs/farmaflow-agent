using System.Security.Cryptography;
using System.Text.Json;

namespace FarmaFlow.Server.Host;

public sealed record ServerSecrets(string DatabasePassword, string JwtSecret, string NextAuthSecret, string BackupKey)
{
    public static ServerSecrets Load(ServerHostOptions options)
    {
        string path = Path.Combine(options.DataDirectory, "secrets.json");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Configuração segura não encontrada: {path}");

        return JsonSerializer.Deserialize<ServerSecrets>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("A configuração segura do servidor é inválida.");
    }

    public static string GenerateSecret(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
}
