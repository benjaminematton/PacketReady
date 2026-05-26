using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PacketReady.Domain.Intake;
using PacketReady.Domain.Providers;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class IntakeSessionConfiguration : IEntityTypeConfiguration<IntakeSession>
{
    public void Configure(EntityTypeBuilder<IntakeSession> b)
    {
        b.ToTable("intake_sessions", t =>
        {
            // Pin the five FSM states from design.md §7.3. PascalCase matches
            // the C# enum name (conventions.md §1: domain-state enum, no
            // external authority).
            t.HasCheckConstraint(
                "ck_intake_sessions_state_values",
                "state IN ('Pending', 'AwaitingProvider', 'AgentProcessing', 'Complete', 'Escalated')");

            // Belt on IntakeSession.Start's invariants; protects against raw
            // SQL backfills.
            t.HasCheckConstraint(
                "ck_intake_sessions_turn_budget_positive",
                "turn_budget >= 1");

            t.HasCheckConstraint(
                "ck_intake_sessions_turns_consumed_non_negative",
                "turns_consumed >= 0");

            // The 9th turn rule: orchestrator escalates when consumed >= budget.
            // The CHECK pins that the aggregate never advanced past the cap.
            t.HasCheckConstraint(
                "ck_intake_sessions_turns_within_budget",
                "turns_consumed <= turn_budget");

            // The FSM is two columns (state enum + state_payload JSONB) that
            // must agree. The aggregate keeps them in sync; this CHECK pins
            // the invariant at the schema level so a hand-rolled UPDATE
            // (or a future migration that touches one column) can't desync.
            t.HasCheckConstraint(
                "ck_intake_sessions_state_matches_payload_kind",
                "(state_payload->>'kind') = state");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();

        // Domain-state enum: default HasConversion<string>() writes PascalCase
        // member names verbatim. MaxLength = 24 leaves headroom (longest current
        // value is 'AwaitingProvider' = 16); width is a typo guard, not storage.
        b.Property(x => x.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();

        // ProviderState union serializes here via STJ polymorphism (kind
        // discriminator + per-variant fields). Same JSONB pattern as
        // Provider.ProfileJson.
        b.Property(x => x.StatePayloadJson)
            .HasColumnName("state_payload")
            .HasColumnType("jsonb")
            .IsRequired();

        b.Property(x => x.TurnsConsumed).HasColumnName("turns_consumed").IsRequired();
        b.Property(x => x.TurnBudget).HasColumnName("turn_budget").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.LastTransitionAt).HasColumnName("last_transition_at").IsRequired();

        // One row per provider. The orchestrator's FOR UPDATE row lock plus this
        // unique constraint together make "two concurrent turns for the same
        // provider" impossible (phase-5 doc, Risks → "Concurrent turns").
        b.HasIndex(x => x.ProviderId)
            .IsUnique()
            .HasDatabaseName("ux_intake_sessions_provider");

        // "Find sessions stuck in AwaitingProvider for > N days" — the future
        // reminder job's read pattern. (state, last_transition_at) is the
        // composite that answers without a JSON path.
        b.HasIndex(x => new { x.State, x.LastTransitionAt })
            .HasDatabaseName("ix_intake_sessions_state_last_transition");

        b.HasOne<Provider>()
            .WithOne()
            .HasForeignKey<IntakeSession>(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
