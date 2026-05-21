namespace PacketReady.Domain.Providers;

/// <summary>
/// DEA registration snapshot.
///
/// <para><b>Record equality caveat:</b> <see cref="Schedules"/> is an
/// <see cref="IReadOnlyList{T}"/>, so record <c>==</c> falls back to reference
/// equality on the list — two <see cref="DeaInfo"/> instances built from equal-but-distinct
/// schedule lists will not compare equal. We round-trip via JSON, never via in-memory
/// equality, so this is documented rather than worked around.</para>
/// </summary>
public sealed record DeaInfo(
    string Number,
    DateOnly ExpiryDate,
    DeaStatus Status,
    IReadOnlyList<DeaSchedule> Schedules);

public enum DeaStatus { Unknown = 0, Active = 1, Inactive = 2, Expired = 3 }

/// <summary>
/// Controlled-substance schedules a DEA registration may cover. Schedule I has no
/// accepted medical use, so providers do not hold DEA for it; not modeled.
/// </summary>
public enum DeaSchedule
{
    II = 2,
    III = 3,
    IV = 4,
    V = 5,
}
