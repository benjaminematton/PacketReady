using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PacketReady.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    doc_type_conf = table.Column<double>(type: "double precision", nullable: true),
                    classifier_model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    classifier_prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_uri = table.Column<string>(type: "text", nullable: false),
                    original_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                    table.CheckConstraint("ck_documents_doc_type_conf_range", "doc_type_conf IS NULL OR (doc_type_conf >= 0 AND doc_type_conf <= 1)");
                    table.CheckConstraint("ck_documents_doc_type_values", "doc_type IS NULL OR doc_type IN ('License', 'Dea', 'BoardCert', 'Malpractice', 'Cv', 'Other')");
                    table.CheckConstraint("ck_documents_page_count_positive", "page_count >= 1");
                    table.CheckConstraint("ck_documents_uploaded_by_values", "uploaded_by IN ('provider', 'admin')");
                    table.ForeignKey(
                        name: "FK_documents_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_extractions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_id = table.Column<int>(type: "integer", nullable: false),
                    schema_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    field_locations = table.Column<string>(type: "jsonb", nullable: false),
                    confidence = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    edited_by = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    extracted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_extractions", x => x.id);
                    table.CheckConstraint("ck_document_extractions_extraction_id_positive", "extraction_id >= 1");
                    table.CheckConstraint("ck_document_extractions_llm_provenance_pairing", "(source = 'llm' AND model IS NOT NULL AND prompt_hash IS NOT NULL AND edited_by IS NULL) OR (source <> 'llm' AND model IS NULL AND prompt_hash IS NULL AND edited_by IS NOT NULL)");
                    table.CheckConstraint("ck_document_extractions_source_values", "source IN ('llm', 'provider_edit', 'admin_edit')");
                    table.CheckConstraint("ck_document_extractions_status_error_pairing", "(status = 'Succeeded' AND error IS NULL) OR (status = 'Failed' AND error IS NOT NULL)");
                    table.CheckConstraint("ck_document_extractions_status_values", "status IN ('Succeeded', 'Failed')");
                    table.CheckConstraint("ck_document_extractions_token_counts_non_negative", "(input_tokens IS NULL OR input_tokens >= 0) AND (output_tokens IS NULL OR output_tokens >= 0)");
                    table.ForeignKey(
                        name: "FK_document_extractions_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_extractions_doc_schema_extracted",
                table: "document_extractions",
                columns: new[] { "document_id", "schema_version", "extracted_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_document_extractions_doc_extraction",
                table: "document_extractions",
                columns: new[] { "document_id", "extraction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_extractions_idempotency",
                table: "document_extractions",
                columns: new[] { "document_id", "schema_version", "model", "prompt_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_provider_doctype",
                table: "documents",
                columns: new[] { "provider_id", "doc_type" });

            // BEFORE UPDATE immutability trigger. EF can't generate triggers; the
            // shape mirrors audit_events_block_update_delete (P0) but with no scrub
            // escape — extraction rows have no CCPA-style retraction story. Spec
            // §"Document store schema".
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION document_extractions_block_update()
RETURNS TRIGGER AS $$
BEGIN
  RAISE EXCEPTION 'document_extractions is append-only (row %)', OLD.id;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS document_extractions_immutable ON document_extractions;

CREATE TRIGGER document_extractions_immutable
BEFORE UPDATE ON document_extractions
FOR EACH ROW EXECUTE FUNCTION document_extractions_block_update();
");

            // primary_source_results — P5 caller (lookup_primary_source). Landed in
            // P3's migration per spec §"Append primary_source_results in the same
            // migration" so P5 doesn't need a schema-altering follow-up. No EF
            // entity; the table sits empty until P5 wires it.
            migrationBuilder.Sql(@"
CREATE TABLE primary_source_results (
  id                UUID PRIMARY KEY,
  source            TEXT NOT NULL,
  identifiers       JSONB NOT NULL,
  identifiers_hash  TEXT NOT NULL,
  result            JSONB NOT NULL,
  status            TEXT NOT NULL,
  turn_id           UUID,
  requested_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT ck_primary_source_results_source_values
    CHECK (source IN ('nppes', 'oig', 'sam', 'state_board', 'caqh')),
  CONSTRAINT ck_primary_source_results_status_values
    CHECK (status IN ('ok', 'not_found', 'error')),
  CONSTRAINT ux_primary_source_results_source_hash
    UNIQUE (source, identifiers_hash)
);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS primary_source_results;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS document_extractions_immutable ON document_extractions;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS document_extractions_block_update();");

            migrationBuilder.DropTable(
                name: "document_extractions");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }
}
