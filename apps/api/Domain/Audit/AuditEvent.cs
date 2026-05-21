using System.Text.Json;

namespace PacketReady.Domain.Audit;

/// <summary>
/// Append-only event row for every action across every provider intake. Backs the
/// per-issue drill-in UX and the Langfuse-paired audit trail.
///
/// <para>Append-only is enforced at the DB level via a <c>BEFORE UPDATE OR DELETE</c>
/// trigger that raises an exception unless <c>app.allow_audit_scrub</c> session GUC
/// is set. A CCPA-style scrub flow would be the only legitimate writer of UPDATEs.</para>
/// </summary>
public class AuditEvent
{
    public Guid Id { get; private set; }

    /// <summary>Nullable: a few event types (e.g. <c>PingExecuted</c>) fire before any provider exists.</summary>
    public Guid? ProviderId { get; private set; }

    /// <summary>Nullable: events outside an intake turn (boot-time, admin actions) have no turn.</summary>
    public Guid? TurnId { get; private set; }

    /// <summary>Plain TEXT, not enum — new event types ship without a migration. See <see cref="AuditEventType"/>.</summary>
    public string EventType { get; private set; } = null!;

    /// <summary>JSONB payload; schema is per-event-type.</summary>
    public string Payload { get; private set; } = "{}";

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Groups events from one logical workflow (e.g. webhook → classify → extract → score).</summary>
    public Guid? CorrelationId { get; private set; }

    private AuditEvent() { }

    public static AuditEvent Create(
        string eventType,
        string payloadJson,
        Guid? providerId = null,
        Guid? turnId = null,
        Guid? correlationId = null,
        DateTimeOffset? occurredAt = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Event type is required.", nameof(eventType));

        var payload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;

        // Fail fast at construction rather than at SaveChanges: invalid JSON would
        // surface as an opaque Npgsql JSONB error far from the bug source.
        try
        {
            using var _ = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Payload must be valid JSON. Got: {payload[..Math.Min(payload.Length, 80)]}",
                nameof(payloadJson), ex);
        }

        return new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Payload = payload,
            ProviderId = providerId,
            TurnId = turnId,
            CorrelationId = correlationId,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        };
    }
}
