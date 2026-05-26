using MediatR;
using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;

namespace PacketReady.Application.Audit.Queries.ListProviderAudit;

public sealed class ListProviderAuditQueryHandler
    : IRequestHandler<ListProviderAuditQuery, IReadOnlyList<AuditEventDto>>
{
    private readonly IAppDbContext _db;

    public ListProviderAuditQueryHandler(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<AuditEventDto>> Handle(
        ListProviderAuditQuery request, CancellationToken ct)
    {
        // Defense in depth: the API endpoint rejects out-of-range limits with
        // a 400 before the request reaches us, but other in-process callers
        // (eval orchestrator, future internal jobs) might pass anything. Clamp
        // so we can't be OOMed by an internal caller asking for a million rows.
        var limit = Math.Clamp(
            request.Limit,
            ListProviderAuditQuery.MinLimit,
            ListProviderAuditQuery.MaxLimit);

        var rows = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.ProviderId == request.ProviderId)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Take(limit)
            .Select(e => new AuditEventDto(
                e.Id,
                e.EventType,
                e.OccurredAt,
                e.Payload,
                e.TurnId,
                e.CorrelationId))
            .ToListAsync(ct);

        return rows;
    }
}
