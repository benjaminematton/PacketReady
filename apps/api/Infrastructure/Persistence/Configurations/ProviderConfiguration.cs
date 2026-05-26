using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PacketReady.Domain.Providers;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class ProviderConfiguration : IEntityTypeConfiguration<Provider>
{
    public void Configure(EntityTypeBuilder<Provider> b)
    {
        b.ToTable("providers", t =>
        {
            // Floor matches the aggregate's invariant
            // (IntakeSession.Start refuses turnBudget < 1). The CHECK is
            // belt-and-braces against raw SQL inserts; the .NET caller
            // already throws ArgumentOutOfRangeException on the same
            // boundary.
            t.HasCheckConstraint(
                "ck_providers_intake_budget_turns_positive",
                "intake_budget_turns >= 1");
        });
        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        // P4 payer routing. Opaque string keyed against YAML files in
        // `apps/api/Infrastructure/Payers/payers/*.yaml`. The DB default
        // backfills pre-P4 rows and protects against raw-SQL inserts that
        // forget to set the column. Resolution / fail-loud on unknown ids
        // happens in PayerRequirementLoader, not here — no FK to a payers
        // table in P4 (the YAMLs are the registry).
        b.Property(x => x.PayerId)
            .HasColumnName("payer_id")
            .HasMaxLength(64)
            .HasDefaultValue(Provider.DefaultPayerId)
            .IsRequired();

        // P5 per-provider agent-turn cap. DefaultValue backfills the
        // column on existing providers (pre-P5 dev rows) and protects
        // raw-SQL inserts. The aggregate refuses < 1 at the boundary,
        // and the CHECK constraint pins it at the schema level.
        b.Property(x => x.IntakeBudgetTurns)
            .HasColumnName("intake_budget_turns")
            .HasDefaultValue(Provider.DefaultIntakeBudgetTurns)
            .IsRequired();

        // Profile is the load-bearing column; everything else is metadata. JSONB,
        // not text, so future Phase 3 query needs (e.g. "providers with no DEA on
        // file") can use Postgres JSON operators without a schema migration.
        b.Property(x => x.ProfileJson)
            .HasColumnName("profile")
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
