namespace PacketReady.Domain.Documents;

/// <summary>
/// Terminal state of a single extraction row. <see cref="Failed"/> rows persist with
/// <c>fields = {}</c>, <c>field_locations = {}</c>, <c>confidence = {}</c>, and a
/// non-null <c>error</c> — failure is data, not exception (spec §"Failure surface").
/// Stored as TEXT; check constraint pins the value set.
/// </summary>
public enum ExtractionStatus
{
    Succeeded,
    Failed,
}
