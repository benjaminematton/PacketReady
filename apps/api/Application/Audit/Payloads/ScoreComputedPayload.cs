using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReady.Domain.Audit;

namespace PacketReady.Application.Audit.Payloads;

/// <summary>
/// Typed payload for <see cref="AuditEventType.ScoreComputed"/>. Lifting this out of
/// an inline anonymous object gives downstream consumers (Langfuse pairing, BI,
/// future drill-in queries on the JSONB column) a single greppable schema, and lets
/// payload-shape changes ride through code review rather than slipping out as a
/// silent diff inside a handler.
///
/// <para>Property names are emitted in <c>snake_case</c> to match the existing
/// JSONB convention established by <c>PingExecuted</c>.</para>
/// </summary>
public sealed record ScoreComputedPayload(
    [property: JsonPropertyName("provider_id")] Guid ProviderId,
    [property: JsonPropertyName("readiness_score_id")] Guid ReadinessScoreId,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("critical_count")] int CriticalCount,
    [property: JsonPropertyName("major_count")] int MajorCount,
    [property: JsonPropertyName("minor_count")] int MinorCount,
    [property: JsonPropertyName("validator_count")] int ValidatorCount,
    [property: JsonPropertyName("issue_count")] int IssueCount,
    // P4 task 14 — count of Issues the ConfidenceGuard downgraded from
    // Critical to Minor on this run. Surfaces guard blast radius into the
    // audit JSONB so an operator can tell "tier moved because of confidence,
    // not credential state" from a row inspection alone.
    [property: JsonPropertyName("low_confidence_downgraded_count")] int LowConfidenceDowngradedCount = 0)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
