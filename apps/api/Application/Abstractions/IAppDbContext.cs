using Microsoft.EntityFrameworkCore;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Abstractions;

/// <summary>
/// Application-layer view of the EF DbContext. Exposes only the DbSets that
/// MediatR handlers and Application-side services need, so Application code
/// doesn't take a hard reference on <c>PacketReadyDbContext</c> (which lives in
/// Infrastructure).
///
/// <para>Inherits <see cref="IUnitOfWork"/> so a handler can read, stage, and
/// commit through one dependency. <see cref="AuditEvents"/> is exposed for
/// <see cref="Audit.IAuditWriter"/> implementations only — handlers MUST route
/// audit writes through <c>IAuditWriter</c> so the staged-vs-independent
/// commit contract stays in one place.</para>
/// </summary>
public interface IAppDbContext : IUnitOfWork
{
    DbSet<Provider> Providers { get; }
    DbSet<ReadinessScore> ReadinessScores { get; }
    DbSet<AuditEvent> AuditEvents { get; }
}
