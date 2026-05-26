using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReady.Domain.Audit;

namespace PacketReady.Application.Intake.Audit;

/// <summary>
/// Typed payload for <see cref="AuditEventType.IntakeTurnCompleted"/>. One
/// row per <c>IntakeTurnJob</c> success — captures the agent's terminal /
/// followup outcome plus the budget-axis consumption so an admin can
/// reconstruct how a turn spent the per-turn budget without correlating
/// across log entries.
/// </summary>
public sealed record IntakeTurnCompletedPayload(
    [property: JsonPropertyName("provider_id")] Guid ProviderId,
    [property: JsonPropertyName("turn_id")] Guid TurnId,
    [property: JsonPropertyName("is_terminal")] bool IsTerminal,
    [property: JsonPropertyName("completed_readiness_score_id")] Guid? CompletedReadinessScoreId,
    [property: JsonPropertyName("queued_outbound_message_id")] Guid? QueuedOutboundMessageId,
    [property: JsonPropertyName("new_magic_link_id")] Guid? NewMagicLinkId,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens,
    [property: JsonPropertyName("wall_clock_ms")] int WallClockMs)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
