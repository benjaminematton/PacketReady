using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReady.Domain.Audit;

namespace PacketReady.Application.Intake.Audit;

/// <summary>
/// Typed payload for <see cref="AuditEventType.IntakeStarted"/>. Matches the
/// <c>ScoreComputedPayload</c> convention — <c>snake_case</c> JSON, single
/// greppable schema, change visible in diff.
///
/// <para>The token itself is NOT logged. Only the link id (which is
/// already in the <c>magic_links</c> table) so a row inspection of
/// <c>audit_events</c> can't be replayed to bypass the signature check.</para>
/// </summary>
public sealed record IntakeStartedPayload(
    [property: JsonPropertyName("provider_id")] Guid ProviderId,
    [property: JsonPropertyName("intake_session_id")] Guid IntakeSessionId,
    [property: JsonPropertyName("magic_link_id")] Guid MagicLinkId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
