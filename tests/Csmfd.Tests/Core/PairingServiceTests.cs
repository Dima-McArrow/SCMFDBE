using Csmfd.Core.Security;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

internal sealed class MemoryTrustStore : ITrustStore
{
    public List<TrustedDevice> Saved { get; private set; } = [];

    public IReadOnlyList<TrustedDevice> Load() => Saved;

    public void Save(IReadOnlyList<TrustedDevice> devices) => Saved = [.. devices];
}

public sealed class PairingServiceTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly MemoryTrustStore _store = new();
    private readonly PairingService _pairing;

    public PairingServiceTests()
    {
        _pairing = new PairingService(_time, _store);
    }

    [Fact]
    public void PairsWithCorrectCodeAndStoresOnlyTheHash()
    {
        var code = _pairing.OpenWindow();
        Assert.True(TextRules.IsPairCodeFormat(code));

        var result = _pairing.TryPair(code, "Dima iPad");

        Assert.Equal(PairOutcome.Paired, result.Outcome);
        Assert.True(TextRules.IsTokenFormat(result.Token!));
        var stored = Assert.Single(_store.Saved);
        Assert.Equal("Dima iPad", stored.Name);
        Assert.DoesNotContain(result.Token!, stored.TokenHash, StringComparison.Ordinal);
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.False(_pairing.IsWindowOpen);
        Assert.Equal(stored.Id, _pairing.Authenticate(result.Token!)!.Id);
    }

    [Fact]
    public void CodeIsSingleUse()
    {
        var code = _pairing.OpenWindow();
        _pairing.TryPair(code, "first");

        Assert.Equal(PairOutcome.Unavailable, _pairing.TryPair(code, "second").Outcome);
    }

    [Fact]
    public void FifthFailureLocksTheWindowForEveryone()
    {
        var code = _pairing.OpenWindow();
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 1; i < PairingService.MaxFailedAttempts; i++)
        {
            Assert.Equal(PairOutcome.Invalid, _pairing.TryPair(wrong, "x").Outcome);
        }

        Assert.Equal(PairOutcome.Locked, _pairing.TryPair(wrong, "x").Outcome);
        Assert.Equal(PairOutcome.Unavailable, _pairing.TryPair(code, "x").Outcome);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public void WindowExpiresAfter120Seconds()
    {
        var code = _pairing.OpenWindow();
        _time.Advance(PairingService.WindowDuration);

        Assert.False(_pairing.IsWindowOpen);
        Assert.Equal(PairOutcome.Unavailable, _pairing.TryPair(code, "x").Outcome);
    }

    [Fact]
    public void ClosedWindowIsUnavailable()
    {
        Assert.Equal(PairOutcome.Unavailable, _pairing.TryPair("123456", "x").Outcome);
    }

    [Fact]
    public void UnknownTokenDoesNotAuthenticate()
    {
        _pairing.TryPair(_pairing.OpenWindow(), "x");

        Assert.Null(_pairing.Authenticate(new string('A', TextRules.TokenLength)));
    }

    [Fact]
    public void RevokeRemovesTrustAndNotifies()
    {
        var result = _pairing.TryPair(_pairing.OpenWindow(), "x");
        var revoked = new List<Guid>();
        _pairing.DeviceRevoked += revoked.Add;

        Assert.True(_pairing.Revoke(result.Device!.Id));

        Assert.Equal([result.Device.Id], revoked);
        Assert.Null(_pairing.Authenticate(result.Token!));
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public void TrustSurvivesRestart()
    {
        var result = _pairing.TryPair(_pairing.OpenWindow(), "x");

        var restarted = new PairingService(_time, _store);

        Assert.NotNull(restarted.Authenticate(result.Token!));
    }
}
