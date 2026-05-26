using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReady.Domain.Audit;

namespace PacketReady.Application.Intake.Audit;

/// <summary>
/// Typed payload for <see cref="AuditEventType.IntakeTurnStarted"/>. One
/// row per <c>IntakeSession.BeginAgentTurn</c> call — paired with the
/// post-turn <see cref="IntakeTurnCompletedPayload"/> so the dashboard
/// can compute per-turn duration (BeginAgentTurn → EndAgentTurn) from
/// the audit timestamps without joining to <c>intake_turns</c>.
///
/// <para><see cref="TurnNumber"/> is the 1-indexed turn count (matches
/// the post-bump <c>TurnsConsumed</c> on the aggregate). Useful for the
/// budget-cap drill-in: "this is turn 8/8 — next escalation is the
/// 9th."</para>
/// </summary>
public sealed record IntakeTurnStartedPayload(
    [property: JsonPropertyName("provider_id")] Guid ProviderId,
    [property: JsonPropertyName("intake_session_id")] Guid IntakeSessionId,
    [property: JsonPropertyName("turn_id")] Guid TurnId,
    [property: JsonPropertyName("turn_number")] int TurnNumber,
    [property: JsonPropertyName("turn_budget")] int TurnBudget)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
