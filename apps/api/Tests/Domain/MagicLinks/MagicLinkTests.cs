using PacketReady.Domain.MagicLinks;
using Xunit;

namespace PacketReady.Tests.Domain.MagicLinks;

public class MagicLinkTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Issue_PopulatesFieldsAndDefaultsTo7DayTtl()
    {
        var link = MagicLink.Issue(ProviderId, T0);

        Assert.NotEqual(Guid.Empty, link.Id);
        Assert.Equal(ProviderId, link.ProviderId);
        Assert.Equal(T0, link.IssuedAt);
        Assert.Equal(T0.AddDays(7), link.ExpiresAt);
        Assert.Null(link.ConsumedAt);
        Assert.Equal(TimeSpan.FromDays(7), MagicLink.DefaultTtl);
    }

    [Fact]
    public void Issue_AcceptsCustomTtl()
    {
        var link = MagicLink.Issue(ProviderId, T0, ttl: TimeSpan.FromHours(1));
        Assert.Equal(T0.AddHours(1), link.ExpiresAt);
    }

    [Fact]
    public void Issue_RejectsEmptyProviderId()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => MagicLink.Issue(Guid.Empty, T0));
        Assert.Equal("providerId", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Issue_RejectsNonPositiveTtl(int seconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MagicLink.Issue(ProviderId, T0, ttl: TimeSpan.FromSeconds(seconds)));
        Assert.Equal("ttl", ex.ParamName);
    }

    // ───────────────────────────────────────────── IsValid ──────

    [Fact]
    public void IsValid_TrueWhenFreshAndUnconsumed()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        Assert.True(link.IsValid(T0.AddMinutes(1)));
        Assert.True(link.IsValid(T0.AddDays(6).AddHours(23)));   // just inside the window
    }

    [Fact]
    public void IsValid_FalseAtAndAfterExpiry()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        Assert.False(link.IsValid(T0.AddDays(7)));        // boundary: expires_at is exclusive
        Assert.False(link.IsValid(T0.AddDays(7).AddSeconds(1)));
    }

    [Fact]
    public void IsValid_FalseOnceConsumed()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        link.Consume(T0.AddMinutes(1));
        Assert.False(link.IsValid(T0.AddMinutes(2)));
    }

    // ───────────────────────────────────────────── Consume ──────

    [Fact]
    public void Consume_StampsConsumedAt()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        var consumedAt = T0.AddMinutes(5);

        link.Consume(consumedAt);

        Assert.Equal(consumedAt, link.ConsumedAt);
    }

    [Fact]
    public void Consume_RefusesDoubleConsume()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        link.Consume(T0.AddMinutes(1));

        var ex = Assert.Throws<InvalidOperationException>(
            () => link.Consume(T0.AddMinutes(2)));
        Assert.Contains("already consumed", ex.Message);
    }

    [Fact]
    public void Consume_RefusesPostExpiry()
    {
        var link = MagicLink.Issue(ProviderId, T0, ttl: TimeSpan.FromMinutes(10));
        var ex = Assert.Throws<InvalidOperationException>(
            () => link.Consume(T0.AddMinutes(11)));
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public void Consume_AcceptsExactlyAtIssuedAt()
    {
        // Boundary: issued_at is the earliest legal consume time. Useful
        // for tests that simulate "click immediately."
        var link = MagicLink.Issue(ProviderId, T0);
        link.Consume(T0);
        Assert.Equal(T0, link.ConsumedAt);
    }
}
