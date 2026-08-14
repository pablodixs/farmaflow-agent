namespace FarmaFlow.Agent;

public sealed class AgentOptions
{
    public int Port { get; init; } = 3333;
    public string ApiBaseUrl { get; init; } = "https://farmaflow-lkhrn.ondigitalocean.app";
    public string WebAppUrl { get; init; } = "https://farmaflow-rho.vercel.app";
    public string[] AllowedOrigins { get; init; } = [];
    public int HeartbeatSeconds { get; init; } = 60;
}
