using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PacketReady.Domain.Messaging;
using PacketReady.Domain.Providers;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class OutboundMessageConfiguration : IEntityTypeConfiguration<OutboundMessage>
{
    public void Configure(EntityTypeBuilder<OutboundMessage> b)
    {
        b.ToTable("outbound_messages", t =>
        {
            t.HasCheckConstraint(
                "ck_outbound_messages_kind_values",
                "kind IN ('IntakeInvitation', 'Followup', 'CompletionNotice')");

            t.HasCheckConstraint(
                "ck_outbound_messages_status_values",
                "status IN ('Queued', 'Sent', 'Cancelled')");

            // Sent rows must carry sent_at; non-Sent rows must not. Catches a
            // mis-updated row that records "Sent" without timestamping the
            // dispatch, which would defeat per-day outbox/sent/ partitioning.
            t.HasCheckConstraint(
                "ck_outbound_messages_sent_at_pairing",
                "(status = 'Sent' AND sent_at IS NOT NULL) OR (status <> 'Sent' AND sent_at IS NULL)");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        b.Property(x => x.TurnId).HasColumnName("turn_id").IsRequired();

        b.Property(x => x.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        b.Property(x => x.ToAddress)
            .HasColumnName("to_address")
            .HasMaxLength(OutboundMessage.MaxToAddressLength)
            .IsRequired();

        b.Property(x => x.Subject)
            .HasColumnName("subject")
            .HasMaxLength(OutboundMessage.MaxSubjectLength)
            .IsRequired();

        b.Property(x => x.Body)
            .HasColumnName("body")
            .IsRequired();

        b.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        b.Property(x => x.HeldUntil).HasColumnName("held_until").IsRequired();
        b.Property(x => x.ComposedAt).HasColumnName("composed_at").IsRequired();
        b.Property(x => x.SentAt).HasColumnName("sent_at");

        // Dedup index: a retried IntakeTurnJob enqueuing the same (provider,
        // turn, kind) hits this and the outbox handler swallows the
        // unique-violation as a "dedup hit" (phase-5 doc, OutboxDispatcherJob).
        // Named so IsUniqueViolation can check by constraint name, matching the
        // ExtractionPersister pattern.
        b.HasIndex(x => new { x.ProviderId, x.TurnId, x.Kind })
            .IsUnique()
            .HasDatabaseName("ux_outbound_messages_dedup");

        // Dispatcher reads "Queued AND held_until <= now()" every 30s — match
        // the access pattern.
        b.HasIndex(x => new { x.Status, x.HeldUntil })
            .HasDatabaseName("ix_outbound_messages_status_held_until");

        // IntakeTurnJob.GetMostRecentToAddressAsync runs WHERE provider_id = @x
        // ORDER BY composed_at DESC LIMIT 1 once per agent turn. Trivial at
        // small scale; degrades to a per-provider seq scan if a long-lived
        // intake accumulates many followups. (provider_id, composed_at) is the
        // matching access path.
        b.HasIndex(x => new { x.ProviderId, x.ComposedAt })
            .HasDatabaseName("ix_outbound_messages_provider_id_composed_at");

        b.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
