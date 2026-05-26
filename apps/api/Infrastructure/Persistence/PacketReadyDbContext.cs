using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Documents;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Messaging;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Infrastructure.Persistence;

public sealed class PacketReadyDbContext : DbContext, IAppDbContext
{
    public PacketReadyDbContext(DbContextOptions<PacketReadyDbContext> options) : base(options) { }

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ReadinessScore> ReadinessScores => Set<ReadinessScore>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentExtraction> DocumentExtractions => Set<DocumentExtraction>();
    public DbSet<IntakeSession> IntakeSessions => Set<IntakeSession>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();
    public DbSet<MagicLink> MagicLinks => Set<MagicLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PacketReadyDbContext).Assembly);
    }

    // IUnitOfWork — DbContext.SaveChangesAsync already returns int, satisfies the contract.
}
