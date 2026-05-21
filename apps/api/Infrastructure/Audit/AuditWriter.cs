using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Domain.Audit;
using PacketReady.Infrastructure.Persistence;

namespace PacketReady.Infrastructure.Audit;

/// <summary>
/// Default <see cref="IAuditWriter"/>. Registered as <c>Scoped</c> so it shares the
/// request-scoped <see cref="IAppDbContext"/> with the rest of the handler:
/// <see cref="Stage"/> attaches to the same context the handler eventually saves on.
/// <see cref="AppendAsync"/> uses <see cref="IDbContextFactory{TContext}"/> to open
/// an independent context and commit immediately, so fire-and-forget telemetry lands
/// even if the caller rolls back.
///
/// <para>Depends on <see cref="IAppDbContext"/> (not the concrete <c>PacketReadyDbContext</c>)
/// so the "single scoped DbContext" invariant is syntactic: anyone who registers a
/// second context would have to register a second <see cref="IAppDbContext"/>, which
/// is a far louder change than swapping a factory.</para>
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    private readonly IAppDbContext _scoped;
    private readonly IDbContextFactory<PacketReadyDbContext> _factory;
    private readonly ILogger<AuditWriter> _logger;

    public AuditWriter(
        IAppDbContext scoped,
        IDbContextFactory<PacketReadyDbContext> factory,
        ILogger<AuditWriter> logger)
    {
        _scoped = scoped;
        _factory = factory;
        _logger = logger;
    }

    public Guid Stage(AuditEvent evt)
    {
        _scoped.AuditEvents.Add(evt);
        return evt.Id;
    }

    public async Task<Guid> AppendAsync(AuditEvent evt, CancellationToken ct)
    {
        try
        {
            await using var independent = await _factory.CreateDbContextAsync(ct);
            independent.AuditEvents.Add(evt);
            await independent.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fire-and-forget telemetry: never throw into the caller. The audit row
            // is lost; the operational signal moves to the structured log instead.
            _logger.LogError(ex,
                "AuditWriter.AppendAsync failed for event_type={EventType} id={Id}",
                evt.EventType, evt.Id);
        }
        return evt.Id;
    }
}
