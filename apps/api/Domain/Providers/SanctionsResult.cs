namespace PacketReady.Domain.Providers;

/// <summary>
/// Snapshot of the OIG and SAM lookup state. The two sources are independent —
/// SanctionsCheckValidator emits per-source Issues so the side-panel can cite
/// whichever check failed (or both).
///
/// <para><see cref="CheckedAt"/> is a <see cref="DateTimeOffset"/> (not <c>DateOnly</c>)
/// because re-checks within a single day are meaningful: an incident response might
/// re-pull OIG after the morning batch, and we need to distinguish the two for audit.</para>
/// </summary>
public sealed record SanctionsResult(
    bool OigClean,
    bool SamClean,
    DateTimeOffset CheckedAt);
