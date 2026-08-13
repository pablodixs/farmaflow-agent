using FarmaFlow.Agent.Services;
using Xunit;

namespace FarmaFlow.Agent.Tests;

public sealed class LocalAccessServiceTests
{
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
