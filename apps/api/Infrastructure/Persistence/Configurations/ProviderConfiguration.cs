using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PacketReady.Domain.Providers;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class ProviderConfiguration : IEntityTypeConfiguration<Provider>
{
    public void Configure(EntityTypeBuilder<Provider> b)
    {
        b.ToTable("providers");
        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        // Profile is the load-bearing column; everything else is metadata. JSONB,
        // not text, so future Phase 3 query needs (e.g. "providers with no DEA on
        // file") can use Postgres JSON operators without a schema migration.
        b.Property(x => x.ProfileJson)
            .HasColumnName("profile")
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
