using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Messaging;

namespace PacketReady.Application.Intake.Audit;

/// <summary>
/// Typed payload for <see cref="AuditEventType.OutboundMessageSent"/>.
/// Records the dispatch receipt — provider/turn correlate the row back to
/// the originating <c>IntakeTurnJob</c>; <c>to_address</c> is captured
/// at-send rather than re-derived so a later Provider rename doesn't
/// retroactively rewrite the audit trail.
/// </summary>
public sealed record OutboundMessageSentPayload(
    [property: JsonPropertyName("outbound_message_id")] Guid OutboundMessageId,
    [property: JsonPropertyName("provider_id")] Guid ProviderId,
    [property: JsonPropertyName("turn_id")] Guid TurnId,
    [property: JsonPropertyName("kind"), JsonConverter(typeof(JsonStringEnumConverter))] MessageKind Kind,
    [property: JsonPropertyName("to_address")] string ToAddress,
    [property: JsonPropertyName("sent_at")] DateTimeOffset SentAt)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
