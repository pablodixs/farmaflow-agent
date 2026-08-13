using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace FarmaFlow.Agent.Infrastructure;

public sealed record AgentRegistration(Guid StationId, string Credential, string ApiBaseUrl);

public sealed class AgentStore
{
    private readonly string _connectionString;

    public AgentStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FarmaFlow", "Agent");
        Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={Path.Combine(directory, "agent.db")}";
        Initialize();
    }

    public AgentRegistration? GetRegistration()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT station_id, credential, api_base_url FROM registration LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var protectedCredential = Convert.FromBase64String(reader.GetString(1));
        var credential = System.Text.Encoding.UTF8.GetString(
            ProtectedData.Unprotect(protectedCredential, null, DataProtectionScope.CurrentUser));
        return new AgentRegistration(Guid.Parse(reader.GetString(0)), credential, reader.GetString(2));
    }

    public void SaveRegistration(AgentRegistration registration)
    {
        var protectedCredential = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(registration.Credential), null, DataProtectionScope.CurrentUser);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM registration; INSERT INTO registration(station_id, credential, api_base_url) VALUES ($id, $credential, $url)";
        command.Parameters.AddWithValue("$id", registration.StationId.ToString());
        command.Parameters.AddWithValue("$credential", Convert.ToBase64String(protectedCredential));
        command.Parameters.AddWithValue("$url", registration.ApiBaseUrl);
        command.ExecuteNonQuery();
    }

    public void Enqueue(string type, object payload)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO outbox(operation_id, type, payload, created_at) VALUES ($id, $type, $payload, $createdAt)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public int PendingCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbox WHERE synced_at IS NULL";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS registration (
                station_id TEXT PRIMARY KEY,
                credential TEXT NOT NULL,
                api_base_url TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS outbox (
                operation_id TEXT PRIMARY KEY,
                type TEXT NOT NULL,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL,
                synced_at TEXT NULL,
                last_error TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
