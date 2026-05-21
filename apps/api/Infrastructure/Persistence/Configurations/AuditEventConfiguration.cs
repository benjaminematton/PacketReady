using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PacketReady.Domain.Audit;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ProviderId).HasColumnName("provider_id");
        b.Property(x => x.TurnId).HasColumnName("turn_id");
        b.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
        b.Property(x => x.CorrelationId).HasColumnName("correlation_id");

        // (ProviderId, OccurredAt) for per-provider timelines.
        b.HasIndex(x => new { x.ProviderId, x.OccurredAt })
            .HasDatabaseName("ix_audit_events_provider_occurred");

        // CorrelationId for pulling a workflow's full event chain.
        b.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_audit_events_correlation");

        // OccurredAt for time-range scans.
        b.HasIndex(x => x.OccurredAt)
            .HasDatabaseName("ix_audit_events_occurred");

        // Append-only trigger is added via a separate hand-written migration after
        // the table migration runs. EF can't generate triggers; do not try to bake
        // the trigger into the fluent config.
    }
}
