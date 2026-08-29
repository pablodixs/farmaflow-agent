using FarmaFlow.Agent.Infrastructure;
using System.Net.Http.Json;

namespace FarmaFlow.Agent.Services;

public sealed class PairingService(HttpClient http, AgentStore store, DesktopConnectionStore connections)
{
    public async Task<PairingResult> PairAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Informe o código de pareamento.");

        var deviceId = $"{Environment.MachineName}:{Environment.UserName}";
        var response = await http.PostAsJsonAsync(
            $"{connections.Load().BackendUrl}/public/agent/pair",
            new { code = code.Trim(), deviceIdentifier = deviceId, operatingSystem = Environment.OSVersion.VersionString, appVersion = Version },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Resposta de pareamento inválida.");
        store.SaveRegistration(new AgentRegistration(
            result.StationId,
            result.Credential,
            connections.Load().BackendUrl));
        return new PairingResult(result.StationId, true);
    }

    public static string Version => typeof(PairingService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    private sealed record PairResponse(Guid StationId, string Credential, string ApiBaseUrl);
}

public sealed record PairingResult(Guid StationId, bool Paired);
