namespace PacketReady.Domain.MagicLinks;

/// <summary>
/// One issued magic-link row. Aggregate root. The JWT-style token the
/// provider receives is signed with the link's <see cref="Id"/>; verification
/// looks up the row by id and checks <see cref="ExpiresAt"/> +
/// <see cref="ConsumedAt"/>.
///
/// <para><b>Lifecycle.</b> <see cref="Issue"/> creates a row with
/// <see cref="ConsumedAt"/> null. <see cref="Consume"/> stamps it on first
/// portal submit — single-use, refused on a second attempt. <b>Replay
/// safety</b>: the consume path needs a transactional <c>SELECT FOR UPDATE</c>
/// + null-check around <see cref="Consume"/> so two concurrent submits
/// can't both succeed. The aggregate refuses double-consume on its own,
/// but the DB lock is what serializes the race
/// (phase-5-intake-agent.md "Magic-link replay" risk).</para>
///
/// <para><b>Re-issue.</b> If the original token expires before the
/// provider clicks it, the admin can issue a fresh row for the same
/// provider — there's no UNIQUE on <c>provider_id</c> in
/// <c>magic_links</c>. The aggregate doesn't know about re-issue; that's
/// the StartIntake / re-issue command's policy.</para>
/// </summary>
public sealed class MagicLink
{
    /// <summary>
    /// Default magic-link lifetime from phase-5-intake-agent.md decisions:
    /// "7 days matches the longest practical 'I'll get to it this weekend'
    /// window without leaving the token live indefinitely." Callers can
    /// override for tests / shorter-lived links.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }

    private MagicLink() { }

    public static MagicLink Issue(
        Guid providerId,
        DateTimeOffset issuedAt,
        TimeSpan? ttl = null)
    {
        if (providerId == Guid.Empty)
            throw new ArgumentException("Provider id is required.", nameof(providerId));

        var lifetime = ttl ?? DefaultTtl;
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(ttl), lifetime, "TTL must be strictly positive.");

        return new MagicLink
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt + lifetime,
            ConsumedAt = null,
        };
    }

    /// <summary>True when the link is still valid (issued, not expired, not consumed).</summary>
    public bool IsValid(DateTimeOffset now)
        => ConsumedAt is null && now < ExpiresAt;

    /// <summary>
    /// Single-use consume. Refused if the link is already consumed (double-click)
    /// or expired (caller should have rejected before getting here). The DB-side
    /// guarantee is a <c>SELECT FOR UPDATE</c> + null-check inside the handler's
    /// transaction; the aggregate enforces the same invariant in-memory.
    /// </summary>
    public void Consume(DateTimeOffset now)
    {
        if (ConsumedAt is not null)
            throw new InvalidOperationException(
                $"Magic link {Id} already consumed at {ConsumedAt:o}.");
        if (now >= ExpiresAt)
            throw new InvalidOperationException(
                $"Magic link {Id} expired at {ExpiresAt:o}; cannot consume at {now:o}.");

        ConsumedAt = now;
    }
}
