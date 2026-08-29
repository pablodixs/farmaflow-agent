namespace FarmaFlow.Agent;

public sealed class AgentOptions
{
    public int Port { get; init; } = 3333;
    public string ApiBaseUrl { get; init; } = "https://127.0.0.1:8443/backend";
    public string WebAppUrl { get; init; } = "https://127.0.0.1:8443";
    public string[] AllowedOrigins { get; init; } = [];
    public int HeartbeatSeconds { get; init; } = 60;
}
