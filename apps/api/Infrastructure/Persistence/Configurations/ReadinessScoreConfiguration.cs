using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PacketReady.Domain.Scoring;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class ReadinessScoreConfiguration : IEntityTypeConfiguration<ReadinessScore>
{
    public void Configure(EntityTypeBuilder<ReadinessScore> b)
    {
        b.ToTable("readiness_scores", t =>
        {
            // Defense-in-depth: ReadinessScore.Create already guards 0..100, but the
            // DB constraint catches anything that bypasses the domain (raw SQL, future
            // alternate writers).
            t.HasCheckConstraint("ck_readiness_scores_score_range", "score BETWEEN 0 AND 100");

            // tier is stored as TEXT (via HasConversion<string> below). Constraint
            // pins the value set so a typo in a future seed won't slip in.
            t.HasCheckConstraint("ck_readiness_scores_tier_values",
                "tier IN ('Red', 'Yellow', 'Green')");

            t.HasCheckConstraint("ck_readiness_scores_counts_non_negative",
                "critical_count >= 0 AND major_count >= 0 AND minor_count >= 0");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        b.Property(x => x.Score).HasColumnName("score").IsRequired();

        // Tier as TEXT, not int. Keeps the column human-readable in psql and matches
        // the doc's check constraint. The enum-to-string conversion is one-to-one;
        // ALL Tier values are valid TEXT values per the CHECK constraint above.
        b.Property(x => x.Tier)
            .HasColumnName("tier")
            .HasConversion<string>()
            .HasMaxLength(8)
            .IsRequired();

        b.Property(x => x.CriticalCount).HasColumnName("critical_count").IsRequired();
        b.Property(x => x.MajorCount).HasColumnName("major_count").IsRequired();
        b.Property(x => x.MinorCount).HasColumnName("minor_count").IsRequired();

        b.Property(x => x.IssuesJson)
            .HasColumnName("issues")
            .HasColumnType("jsonb")
            .IsRequired();

        b.Property(x => x.ComputedAt).HasColumnName("computed_at").IsRequired();

        // FK with cascade on delete — if a provider is hard-deleted, their scores go
        // with them. P1 has no soft-delete; P5 may add status='archived' instead, at
        // which point we'd flip this to Restrict.
        b.HasOne<Domain.Providers.Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Pulls the latest score per provider; matches the dashboard's list query
        // (newest score per provider) and the score-history detail view.
        b.HasIndex(x => new { x.ProviderId, x.ComputedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_readiness_scores_provider_computed");
    }
}
