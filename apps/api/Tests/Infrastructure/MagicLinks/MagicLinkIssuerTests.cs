using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Domain.MagicLinks;
using PacketReady.Infrastructure.MagicLinks;
using PacketReady.Infrastructure.Persistence;
using Xunit;

namespace PacketReady.Tests.Infrastructure.MagicLinks;

public class MagicLinkIssuerTests : IDisposable
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly InMemoryContextFactory _factory;
    private readonly PacketReadyDbContext _db;
    private readonly MagicLinkIssuer _issuer;

    public MagicLinkIssuerTests()
    {
        _factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        _db = _factory.CreateDbContext();
        _issuer = new MagicLinkIssuer(_db, new MagicLinkOptions
        {
            SigningKey = "test-signing-key-with-enough-entropy-for-tests",
        });
    }

    public void Dispose() => _db.Dispose();

    // ─────────────────────────────────────────────── construction ────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_RejectsBlankSigningKey(string key)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new MagicLinkIssuer(_db, new MagicLinkOptions { SigningKey = key }));
    }

    // ────────────────────────────────────────── sign + round-trip ────────

    [Fact]
    public async Task SignToken_RoundTripsToSameLink()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        _db.MagicLinks.Add(link);
        await _db.SaveChangesAsync();

        var token = _issuer.SignToken(link);
        var validated = await _issuer.ValidateAsync(token, T0.AddMinutes(1));

        Assert.Equal(link.Id, validated.Id);
        Assert.Equal(link.ProviderId, validated.ProviderId);
    }

    [Fact]
    public void SignToken_IsDeterministicForSameLink()
    {
        // Same id + same key → same token. Lets the dispatcher (C5) reproduce
        // the URL it emailed without re-storing it.
        var link = MagicLink.Issue(ProviderId, T0);
        var t1 = _issuer.SignToken(link);
        var t2 = _issuer.SignToken(link);
        Assert.Equal(t1, t2);
    }

    [Fact]
    public void SignToken_TokenHasDotSeparator()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        var token = _issuer.SignToken(link);
        Assert.Single(token.Where(c => c == '.').ToList());
    }

    [Fact]
    public void SignToken_RejectsEmptyLinkId()
    {
        // EF rehydrates with Id default-initialized, so a manually-zeroed
        // MagicLink is the realistic shape to reject.
        var ex = Assert.Throws<ArgumentException>(() =>
            _issuer.SignToken(new MagicLinkBuilder().WithEmptyId().Build()));
        Assert.Equal("link", ex.ParamName);
    }

    // ────────────────────────────────────────────── validation ───────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-dot-here")]
    [InlineData(".")]
    [InlineData(".sig")]
    [InlineData("id.")]
    public async Task ValidateAsync_MalformedTokenThrowsMalformed(string token)
    {
        var ex = await Assert.ThrowsAsync<MagicLinkInvalidException>(
            () => _issuer.ValidateAsync(token, T0));
        Assert.Equal(MagicLinkInvalidReason.Malformed, ex.Reason);
    }

    [Fact]
    public async Task ValidateAsync_BadSignatureThrowsBadSignature()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        _db.MagicLinks.Add(link);
        await _db.SaveChangesAsync();

        // Same id, wrong signature segment.
        var token = _issuer.SignToken(link);
        var dotAt = token.IndexOf('.');
        var idPart = token[..dotAt];
        // Use a same-length but different-content sig. 32 bytes → 43 base64url
        // chars (no padding). Filling with 'A' gives a syntactically valid but
        // wrong signature.
        var forged = idPart + "." + new string('A', 43);

        var ex = await Assert.ThrowsAsync<MagicLinkInvalidException>(
            () => _issuer.ValidateAsync(forged, T0));
        Assert.Equal(MagicLinkInvalidReason.BadSignature, ex.Reason);
    }

    [Fact]
    public async Task ValidateAsync_NotFoundWhenRowMissing()
    {
        // Sign a link that's never persisted — signature checks out but
        // SELECT finds nothing.
        var ghost = MagicLink.Issue(ProviderId, T0);
        var token = _issuer.SignToken(ghost);

        var ex = await Assert.ThrowsAsync<MagicLinkInvalidException>(
            () => _issuer.ValidateAsync(token, T0));
        Assert.Equal(MagicLinkInvalidReason.NotFound, ex.Reason);
    }

    [Fact]
    public async Task ValidateAsync_ExpiredThrowsExpired()
    {
        var link = MagicLink.Issue(ProviderId, T0, ttl: TimeSpan.FromMinutes(10));
        _db.MagicLinks.Add(link);
        await _db.SaveChangesAsync();
        var token = _issuer.SignToken(link);

        var ex = await Assert.ThrowsAsync<MagicLinkInvalidException>(
            () => _issuer.ValidateAsync(token, T0.AddMinutes(11)));
        Assert.Equal(MagicLinkInvalidReason.Expired, ex.Reason);
    }

    [Fact]
    public async Task ValidateAsync_ConsumedThrowsConsumed()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        link.Consume(T0.AddMinutes(1));
        _db.MagicLinks.Add(link);
        await _db.SaveChangesAsync();
        var token = _issuer.SignToken(link);

        var ex = await Assert.ThrowsAsync<MagicLinkInvalidException>(
            () => _issuer.ValidateAsync(token, T0.AddMinutes(2)));
        Assert.Equal(MagicLinkInvalidReason.Consumed, ex.Reason);
    }

    [Fact]
    public async Task ValidateAsync_ConsumedBeatsExpiredWhenBothTrue()
    {
        // If a link is both consumed AND past expiry, we report Consumed —
        // that signal is more actionable for the provider ("you used this")
        // than Expired ("this got too old").
        var link = MagicLink.Issue(ProviderId, T0, ttl: TimeSpan.FromMinutes(10));
        link.Consume(T0.AddMinutes(1));
        _db.MagicLinks.Add(link);
        await _db.SaveChangesAsync();
        var token = _issuer.SignToken(link);

        var ex = await Assert.ThrowsAsync<MagicLinkInvalidException>(
            () => _issuer.ValidateAsync(token, T0.AddMinutes(20)));
        Assert.Equal(MagicLinkInvalidReason.Consumed, ex.Reason);
    }

    [Fact]
    public async Task ValidateAsync_DifferentSigningKeysRejectEachOthersTokens()
    {
        var link = MagicLink.Issue(ProviderId, T0);
        _db.MagicLinks.Add(link);
        await _db.SaveChangesAsync();

        var tokenFromOurs = _issuer.SignToken(link);

        // Same DB, different secret → bad signature, even on a known-good link.
        var theirs = new MagicLinkIssuer(_db, new MagicLinkOptions
        {
            SigningKey = "a-different-signing-key",
        });
        var ex = await Assert.ThrowsAsync<MagicLinkInvalidException>(
            () => theirs.ValidateAsync(tokenFromOurs, T0.AddMinutes(1)));
        Assert.Equal(MagicLinkInvalidReason.BadSignature, ex.Reason);
    }

    // Helper to construct a MagicLink with a forced empty Id for the
    // SignToken empty-id rejection test, without exposing a setter on the
    // domain type.
    private sealed class MagicLinkBuilder
    {
        private MagicLink _link = MagicLink.Issue(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            T0);

        public MagicLinkBuilder WithEmptyId()
        {
            // EF rehydrates via the private ctor + reflection; tests can use
            // the same path. Mark on the property via reflection so the
            // builder doesn't depend on a public setter.
            typeof(MagicLink)
                .GetProperty(nameof(MagicLink.Id))!
                .SetValue(_link, Guid.Empty);
            return this;
        }

        public MagicLink Build() => _link;
    }
}
