using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FarmaFlow.Agent.Services;

public sealed class LocalAccessService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _challenges = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new();

    public string CreateChallenge()
    {
        Cleanup();
        var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _challenges[challenge] = DateTimeOffset.UtcNow.AddMinutes(2);
        return challenge;
    }

    public string Exchange(string challenge)
    {
        if (!_challenges.TryRemove(challenge, out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Desafio local inválido ou expirado.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _tokens[token] = DateTimeOffset.UtcNow.AddHours(12);
        return token;
    }

    public bool IsValid(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var token = authorization[7..];
        return _tokens.TryGetValue(token, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _challenges.Where(x => x.Value <= now)) _challenges.TryRemove(item.Key, out _);
        foreach (var item in _tokens.Where(x => x.Value <= now)) _tokens.TryRemove(item.Key, out _);
    }
}
