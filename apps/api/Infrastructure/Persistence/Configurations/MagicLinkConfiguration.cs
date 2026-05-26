using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Providers;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class MagicLinkConfiguration : IEntityTypeConfiguration<MagicLink>
{
    public void Configure(EntityTypeBuilder<MagicLink> b)
    {
        b.ToTable("magic_links", t =>
        {
            t.HasCheckConstraint(
                "ck_magic_links_expires_after_issued",
                "expires_at > issued_at");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();
        b.Property(x => x.IssuedAt).HasColumnName("issued_at").IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        b.Property(x => x.ConsumedAt).HasColumnName("consumed_at");

        // Per-provider lookups for "issue a fresh link" + "find the active
        // link for this session." Compound (provider, issued_at desc) supports
        // "latest link for provider X" without a sort.
        b.HasIndex(x => new { x.ProviderId, x.IssuedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_magic_links_provider_issued");

        b.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
