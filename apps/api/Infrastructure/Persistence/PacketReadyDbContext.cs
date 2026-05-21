using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Infrastructure.Persistence;

public sealed class PacketReadyDbContext : DbContext, IAppDbContext
{
    public PacketReadyDbContext(DbContextOptions<PacketReadyDbContext> options) : base(options) { }

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ReadinessScore> ReadinessScores => Set<ReadinessScore>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PacketReadyDbContext).Assembly);
    }

    // IUnitOfWork — DbContext.SaveChangesAsync already returns int, satisfies the contract.
}
