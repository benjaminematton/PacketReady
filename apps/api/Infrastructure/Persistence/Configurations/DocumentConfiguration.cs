using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    // External-identifier enum: stored lowercase per spec (`'provider' | 'admin'`).
    // See docs/conventions.md §3 for the wiring template and §1 for when to pick
    // this path over `HasConversion<string>()`. Helpers throw on unmapped values
    // so adding a third Uploader without updating the converter fails at the
    // write boundary instead of silently routing to a default branch.
    private static readonly ValueConverter<Uploader, string> UploaderConverter = new(
        v => ToColumn(v),
        s => FromColumn(s));

    private static string ToColumn(Uploader v) => v switch
    {
        Uploader.Provider => "provider",
        Uploader.Admin => "admin",
        _ => throw new InvalidOperationException($"Unmapped Uploader value: {v}"),
    };

    private static Uploader FromColumn(string s) => s switch
    {
        "provider" => Uploader.Provider,
        "admin" => Uploader.Admin,
        _ => throw new InvalidOperationException($"Unmapped uploaded_by value: '{s}'"),
    };

    public void Configure(EntityTypeBuilder<Document> b)
    {
        b.ToTable("documents", t =>
        {
            // doc_type is nullable (classifier failure leaves it unset). The
            // constraint allows NULL or one of the pinned values; a typo in a
            // future seed would otherwise silently land.
            t.HasCheckConstraint(
                "ck_documents_doc_type_values",
                "doc_type IS NULL OR doc_type IN ('License', 'Dea', 'BoardCert', 'Malpractice', 'Cv', 'Other')");

            t.HasCheckConstraint(
                "ck_documents_uploaded_by_values",
                "uploaded_by IN ('provider', 'admin')");

            // 0..1 self-report from Haiku. NULL allowed (paired with doc_type
            // nullability above).
            t.HasCheckConstraint(
                "ck_documents_doc_type_conf_range",
                "doc_type_conf IS NULL OR (doc_type_conf >= 0 AND doc_type_conf <= 1)");

            // Belt-and-braces on Document.Create's PageCount >= 1 invariant.
            t.HasCheckConstraint(
                "ck_documents_page_count_positive",
                "page_count >= 1");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ProviderId).HasColumnName("provider_id").IsRequired();

        // Default enum-to-string conversion writes PascalCase ('License', 'Dea', …)
        // — matches the spec exactly. MaxLength = 32 leaves headroom for future
        // doc types (longest current value is 'Malpractice' = 11); width matters
        // only for catching corrupt seed data, not for storage.
        b.Property(x => x.DocType)
            .HasColumnName("doc_type")
            .HasConversion<string>()
            .HasMaxLength(32);

        b.Property(x => x.DocTypeConfidence).HasColumnName("doc_type_conf");

        b.Property(x => x.ClassifierModel)
            .HasColumnName("classifier_model")
            .HasMaxLength(64)
            .IsRequired();

        b.Property(x => x.ClassifierPromptHash)
            .HasColumnName("classifier_prompt_hash")
            .HasMaxLength(64)
            .IsRequired();

        b.Property(x => x.StorageUri)
            .HasColumnName("storage_uri")
            .IsRequired();

        b.Property(x => x.OriginalName)
            .HasColumnName("original_name")
            .HasMaxLength(255)
            .IsRequired();

        b.Property(x => x.MimeType)
            .HasColumnName("mime_type")
            .HasMaxLength(64)
            .IsRequired();

        b.Property(x => x.PageCount).HasColumnName("page_count").IsRequired();
        b.Property(x => x.UploadedAt).HasColumnName("uploaded_at").IsRequired();

        b.Property(x => x.UploadedBy)
            .HasColumnName("uploaded_by")
            .HasConversion(UploaderConverter)
            .HasMaxLength(16)
            .IsRequired();

        // Cascade: if a provider is hard-deleted, their documents go too — same
        // policy as readiness_scores.
        b.HasOne<Domain.Providers.Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Per-provider doc-type lookup (aggregator reads "latest license for
        // provider X" repeatedly).
        b.HasIndex(x => new { x.ProviderId, x.DocType })
            .HasDatabaseName("ix_documents_provider_doctype");
    }
}
