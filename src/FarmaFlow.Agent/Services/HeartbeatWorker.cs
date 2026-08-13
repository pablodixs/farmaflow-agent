using FarmaFlow.Agent.Infrastructure;
using System.Net.Http.Json;

namespace FarmaFlow.Agent.Services;

public sealed class HeartbeatWorker(HttpClient http, AgentStore store, AgentOptions options, ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, options.HeartbeatSeconds)));
        do
        {
            var registration = store.GetRegistration();
            if (registration is not null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{registration.ApiBaseUrl.TrimEnd('/')}/public/agent/heartbeat")
                    {
                        Content = JsonContent.Create(new
                        {
                            stationId = registration.StationId,
                            deviceIdentifier = $"{Environment.MachineName}:{Environment.UserName}",
                            operatingSystem = Environment.OSVersion.VersionString,
                            appVersion = PairingService.Version,
                            pendingOperations = store.PendingCount()
                        })
                    };
                    request.Headers.Add("X-Agent-Credential", registration.Credential);
                    using var response = await http.SendAsync(request, stoppingToken);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Não foi possível enviar heartbeat.");
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
