using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReady.Domain.Audit;

namespace PacketReady.Application.Intake.Audit;

/// <summary>
/// Typed payload for <see cref="AuditEventType.IntakeCompleted"/>. Stamped
/// when the agent invokes <c>compute_readiness</c> (the terminal tool)
/// and the FSM transitions <c>AgentProcessing → Complete</c>. The
/// readiness-score row is written by <c>ComputeReadinessScoreCommand</c>
/// before this event lands; <see cref="ReadinessScoreId"/> ties the
/// audit walk back to the score blob.
/// </summary>
public sealed record IntakeCompletedPayload(
    [property: JsonPropertyName("provider_id")] Guid ProviderId,
    [property: JsonPropertyName("intake_session_id")] Guid IntakeSessionId,
    [property: JsonPropertyName("turn_id")] Guid TurnId,
    [property: JsonPropertyName("readiness_score_id")] Guid ReadinessScoreId,
    [property: JsonPropertyName("turns_consumed")] int TurnsConsumed)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
