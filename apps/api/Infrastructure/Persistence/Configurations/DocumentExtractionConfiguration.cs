using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Persistence.Configurations;

public sealed class DocumentExtractionConfiguration : IEntityTypeConfiguration<DocumentExtraction>
{
    // External-identifier enum: stored lowercase per spec
    // (`'llm' | 'provider_edit' | 'admin_edit'`). See docs/conventions.md §3 for
    // the wiring template and §1 for when to pick this path over
    // `HasConversion<string>()`. Helpers throw on unmapped values so a fourth
    // ExtractionSource added without updating this converter fails at the write
    // boundary instead of silently routing to a default branch.
    private static readonly ValueConverter<ExtractionSource, string> SourceConverter = new(
        v => ToColumn(v),
        s => FromColumn(s));

    private static string ToColumn(ExtractionSource v) => v switch
    {
        ExtractionSource.Llm => "llm",
        ExtractionSource.ProviderEdit => "provider_edit",
        ExtractionSource.AdminEdit => "admin_edit",
        _ => throw new InvalidOperationException($"Unmapped ExtractionSource value: {v}"),
    };

    private static ExtractionSource FromColumn(string s) => s switch
    {
        "llm" => ExtractionSource.Llm,
        "provider_edit" => ExtractionSource.ProviderEdit,
        "admin_edit" => ExtractionSource.AdminEdit,
        _ => throw new InvalidOperationException($"Unmapped source value: '{s}'"),
    };

    public void Configure(EntityTypeBuilder<DocumentExtraction> b)
    {
        b.ToTable("document_extractions", t =>
        {
            t.HasCheckConstraint(
                "ck_document_extractions_status_values",
                "status IN ('Succeeded', 'Failed')");

            t.HasCheckConstraint(
                "ck_document_extractions_source_values",
                "source IN ('llm', 'provider_edit', 'admin_edit')");

            // 1-indexed per spec §"Why per-document extraction_id".
            t.HasCheckConstraint(
                "ck_document_extractions_extraction_id_positive",
                "extraction_id >= 1");

            // Domain factories already enforce these cross-field invariants; the
            // CHECK constraints are the floor that catches raw SQL backfills and
            // any future caller that bypasses the aggregate root.

            // Status ↔ error: Failed rows MUST carry a reason; Succeeded rows
            // MUST NOT (a non-null error on a Succeeded row would be a silent
            // contradiction the aggregator can't recover from).
            t.HasCheckConstraint(
                "ck_document_extractions_status_error_pairing",
                "(status = 'Succeeded' AND error IS NULL) OR (status = 'Failed' AND error IS NOT NULL)");

            // Source = 'llm' ↔ model present + edited_by absent. The unique
            // idempotency index relies on model = NULL skipping dedup on
            // edit rows; this constraint protects that invariant.
            t.HasCheckConstraint(
                "ck_document_extractions_llm_provenance_pairing",
                "(source = 'llm' AND model IS NOT NULL AND prompt_hash IS NOT NULL AND edited_by IS NULL) "
                + "OR (source <> 'llm' AND model IS NULL AND prompt_hash IS NULL AND edited_by IS NOT NULL)");

            // Token counts: when present, must be non-negative.
            t.HasCheckConstraint(
                "ck_document_extractions_token_counts_non_negative",
                "(input_tokens IS NULL OR input_tokens >= 0) AND (output_tokens IS NULL OR output_tokens >= 0)");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.DocumentId).HasColumnName("document_id").IsRequired();
        b.Property(x => x.ExtractionId).HasColumnName("extraction_id").IsRequired();

        b.Property(x => x.SchemaVersion)
            .HasColumnName("schema_version")
            .HasMaxLength(32)
            .IsRequired();

        b.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        b.Property(x => x.FieldsJson)
            .HasColumnName("fields")
            .HasColumnType("jsonb")
            .IsRequired();

        b.Property(x => x.FieldLocationsJson)
            .HasColumnName("field_locations")
            .HasColumnType("jsonb")
            .IsRequired();

        b.Property(x => x.ConfidenceJson)
            .HasColumnName("confidence")
            .HasColumnType("jsonb")
            .IsRequired();

        b.Property(x => x.Error).HasColumnName("error");

        b.Property(x => x.Source)
            .HasColumnName("source")
            .HasConversion(SourceConverter)
            .HasMaxLength(16)
            .IsRequired();

        b.Property(x => x.EditedBy).HasColumnName("edited_by");

        // All four are nullable: LLM rows always populate them; P5 manual-edit
        // rows leave model + prompt_hash NULL, which is what makes the Postgres
        // UNIQUE constraint below skip dedup on edit rows by design.
        b.Property(x => x.Model).HasColumnName("model").HasMaxLength(64);
        b.Property(x => x.PromptHash).HasColumnName("prompt_hash").HasMaxLength(64);
        b.Property(x => x.InputTokens).HasColumnName("input_tokens");
        b.Property(x => x.OutputTokens).HasColumnName("output_tokens");

        b.Property(x => x.ExtractedAt).HasColumnName("extracted_at").IsRequired();
        b.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");

        b.HasOne<Document>()
            .WithMany()
            .HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Belt on the per-document extraction_id allocator: handler takes
        // pg_advisory_xact_lock; this constraint catches any caller that skips
        // the lock.
        b.HasIndex(x => new { x.DocumentId, x.ExtractionId })
            .IsUnique()
            .HasDatabaseName("ux_document_extractions_doc_extraction");

        // Idempotency: identical (document, schema_version, model, prompt_hash)
        // tuples are deduped. Postgres treats NULL as distinct, so manual-edit
        // rows (model = NULL) never dedup — by design (spec §"Why the unique-by").
        b.HasIndex(x => new { x.DocumentId, x.SchemaVersion, x.Model, x.PromptHash })
            .IsUnique()
            .HasDatabaseName("ux_document_extractions_idempotency");

        // Aggregator reads "latest succeeded extraction for document X, schema
        // license.v1" repeatedly — match the access pattern.
        b.HasIndex(x => new { x.DocumentId, x.SchemaVersion, x.ExtractedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_document_extractions_doc_schema_extracted");

        // Append-only trigger is added via the hand-edited tail of the
        // AddDocumentStore migration — EF can't generate triggers, do not bake
        // it into the fluent config.
    }
}
