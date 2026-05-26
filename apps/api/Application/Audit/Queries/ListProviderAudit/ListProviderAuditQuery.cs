using MediatR;

namespace PacketReady.Application.Audit.Queries.ListProviderAudit;

/// <summary>
/// Returns the audit-event chain for one provider, ordered chronologically.
/// Used by the dashboard's "Why we flagged this" tab to render the
/// classify → extract → validator → score timeline behind every Issue.
///
/// <para>Scoped to a single provider + capped at <paramref name="Limit"/> rows
/// so the dashboard doesn't have to paginate. The cap also bounds the response
/// size for a hot provider (50 packets × N extractions × scoreruns).</para>
/// </summary>
public sealed record ListProviderAuditQuery(Guid ProviderId, int Limit = DefaultLimit)
    : IRequest<IReadOnlyList<AuditEventDto>>
{
    public const int DefaultLimit = 100;
    public const int MinLimit = 1;
    public const int MaxLimit = 500;
}

public sealed record AuditEventDto(
    Guid Id,
    string EventType,
    DateTimeOffset OccurredAt,
    string Payload,                 // raw JSONB string; dashboard parses what it needs
    Guid? TurnId,
    Guid? CorrelationId);
