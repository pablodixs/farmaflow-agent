using FarmaFlow.Agent.Services;
using FarmaFlow.Agent;
using Xunit;

namespace FarmaFlow.Agent.Tests;

public sealed class LocalAccessServiceTests
{
    [Fact]
    public void EmbedsTrayIconInApplicationAssembly()
    {
        using var stream = typeof(TrayApplicationContext).Assembly
            .GetManifestResourceStream("FarmaFlow.Agent.Assets.farmaflow.ico");

        Assert.NotNull(stream);
    }

    [Fact]
    public void ExchangesChallengeOnceAndAcceptsBearerToken()
    {
        var service = new LocalAccessService();
        var challenge = service.CreateChallenge();

        var token = service.Exchange(challenge);

        Assert.True(service.IsValid($"Bearer {token}"));
        Assert.Throws<InvalidOperationException>(() => service.Exchange(challenge));
    }
}
