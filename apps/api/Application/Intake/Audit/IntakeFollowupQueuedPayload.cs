using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReady.Domain.Audit;

namespace PacketReady.Application.Intake.Audit;

/// <summary>
/// Typed payload for <see cref="AuditEventType.IntakeFollowupQueued"/>.
/// Stamped when an agent turn proposes a followup: the FSM transitions
/// <c>AgentProcessing → AwaitingProvider</c>, a fresh <c>MagicLink</c>
/// is issued, and an <c>OutboundMessage</c> lands in the outbox in
/// status=<c>Queued</c>. The dispatcher sends the email on a later
/// tick — that emit lands as <c>OutboundMessageSent</c>.
/// </summary>
public sealed record IntakeFollowupQueuedPayload(
    [property: JsonPropertyName("provider_id")] Guid ProviderId,
    [property: JsonPropertyName("intake_session_id")] Guid IntakeSessionId,
    [property: JsonPropertyName("turn_id")] Guid TurnId,
    [property: JsonPropertyName("new_magic_link_id")] Guid NewMagicLinkId,
    [property: JsonPropertyName("queued_outbound_message_id")] Guid QueuedOutboundMessageId)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
