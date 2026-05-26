using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Domain.Documents;

namespace PacketReady.Application.Intake.Agent.Tools;

/// <summary>
/// <c>extract_fields</c> — surface the latest succeeded extraction for one
/// document at a specific schema. Read-only in C4; a future enhancement
/// could re-extract on cache miss, but for the demo the upload pipeline
/// already extracts at <c>POST /api/providers/{id}/documents</c>.
///
/// <para><b>Input:</b> <c>{ document_id, schema }</c> where <c>schema</c>
/// is one of <c>license | dea | malpractice | board_cert | cv</c>.<br/>
/// <b>Output:</b> <c>{ fields, field_locations, confidence }</c> — or
/// <c>{ error }</c> on miss.</para>
///
/// <para><b>Why both this and <c>read_document</c>?</b> Schema-targeted
/// reads support the "this doc looks like a license but I'm not sure"
/// case — the agent can ask for the license-schema view of a doc the
/// classifier put in a different bucket.</para>
/// </summary>
public sealed class ExtractFieldsTool : IIntakeTool
{
    public string Name => "extract_fields";

    public string Description =>
        "Read the extracted fields for one document against a specific schema (license/dea/malpractice/board_cert/cv). Use when read_document gave you a doc_type you don't trust and you want to see what a different schema reading would produce.";

    private static readonly JsonElement _schema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["document_id", "schema"],
          "properties": {
            "document_id": {
              "type": "string",
              "description": "UUID of the documents row to read."
            },
            "schema": {
              "type": "string",
              "enum": ["license", "dea", "malpractice", "board_cert", "cv"],
              "description": "Which schema's view of the document to surface."
            }
          }
        }
        """).RootElement;

    public JsonElement InputSchema => _schema;

    private readonly IAppDbContext _db;

    public ExtractFieldsTool(IAppDbContext db) { _db = db; }

    public async Task<JsonElement> InvokeAsync(
        JsonElement args,
        Guid providerId,
        Guid turnId,
        CancellationToken ct)
    {
        if (!ToolArgs.TryReadGuid(args, "document_id", out var documentId))
            return ToolResults.Error("document_id is required and must be a UUID.");
        if (!ToolArgs.TryReadString(args, "schema", out var schema))
            return ToolResults.Error("schema is required.");

        // Schema names → P3's schema_version stamp. The extractor base
        // class stamps "license.v1" etc on the row; agent-facing names
        // drop the version suffix.
        var schemaVersionPrefix = schema + ".";

        var doc = await _db.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null || doc.ProviderId != providerId)
            return ToolResults.Error($"No document {documentId} for this provider.");

        var extraction = await _db.DocumentExtractions
            .AsNoTracking()
            .Where(e => e.DocumentId == documentId
                     && e.Status == ExtractionStatus.Succeeded
                     && e.SchemaVersion.StartsWith(schemaVersionPrefix))
            .OrderByDescending(e => e.ExtractedAt)
            .FirstOrDefaultAsync(ct);

        if (extraction is null)
            return ToolResults.Error(
                $"Document {documentId} has no '{schema}' extraction yet. Try read_document to see what schema was used.");

        using var fields = JsonDocument.Parse(extraction.FieldsJson);
        using var locations = JsonDocument.Parse(extraction.FieldLocationsJson);
        using var confidence = JsonDocument.Parse(extraction.ConfidenceJson);

        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WritePropertyName("fields");
            fields.RootElement.WriteTo(w);
            w.WritePropertyName("field_locations");
            locations.RootElement.WriteTo(w);
            w.WritePropertyName("confidence");
            confidence.RootElement.WriteTo(w);
            w.WriteEndObject();
        }

        return JsonDocument.Parse(buf.WrittenSpan.ToArray()).RootElement;
    }
}
