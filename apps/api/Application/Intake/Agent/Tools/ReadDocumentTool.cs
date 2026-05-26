using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Domain.Documents;

namespace PacketReady.Application.Intake.Agent.Tools;

/// <summary>
/// <c>read_document</c> — surface the latest succeeded extraction for one
/// document so the agent can reason about it without re-running Sonnet.
/// Read-only; never hits the LLM.
///
/// <para><b>Input:</b> <c>{ document_id: string (uuid) }</c><br/>
/// <b>Output:</b> <c>{ doc_type, extracted_fields, field_locations }</c>
/// — or <c>{ error }</c> when the document / extraction is missing.
/// Errors are returned as part of the tool result (not exceptions) so
/// the agent can reason about a missing doc and propose a followup.</para>
/// </summary>
public sealed class ReadDocumentTool : IIntakeTool
{
    public string Name => "read_document";

    public string Description =>
        "Read a previously-uploaded document and return its current extracted fields plus per-field source locations. Use when you need to inspect a document's contents before deciding whether to ask the provider for more.";

    private static readonly JsonElement _schema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["document_id"],
          "properties": {
            "document_id": {
              "type": "string",
              "description": "UUID of the documents row to read."
            }
          }
        }
        """).RootElement;

    public JsonElement InputSchema => _schema;

    private readonly IAppDbContext _db;

    public ReadDocumentTool(IAppDbContext db) { _db = db; }

    public async Task<JsonElement> InvokeAsync(
        JsonElement args,
        Guid providerId,
        Guid turnId,
        CancellationToken ct)
    {
        if (!ToolArgs.TryReadGuid(args, "document_id", out var documentId))
            return ToolResults.Error("document_id is required and must be a UUID.");

        var doc = await _db.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null || doc.ProviderId != providerId)
            return ToolResults.Error($"No document {documentId} for this provider.");

        // Latest succeeded extraction, most recently extracted; the
        // aggregator uses the same "latest per (doc, schema)" rule.
        var extraction = await _db.DocumentExtractions
            .AsNoTracking()
            .Where(e => e.DocumentId == documentId
                     && e.Status == ExtractionStatus.Succeeded)
            .OrderByDescending(e => e.ExtractedAt)
            .FirstOrDefaultAsync(ct);

        if (extraction is null)
            return ToolResults.Error($"Document {documentId} has no succeeded extraction yet.");

        // Build the response by composing pre-validated JSONB strings into
        // one object — no re-serialize, no re-parse.
        using var fields = JsonDocument.Parse(extraction.FieldsJson);
        using var locations = JsonDocument.Parse(extraction.FieldLocationsJson);

        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("doc_type", doc.DocType?.ToString() ?? "Unknown");
            w.WritePropertyName("extracted_fields");
            fields.RootElement.WriteTo(w);
            w.WritePropertyName("field_locations");
            locations.RootElement.WriteTo(w);
            w.WriteEndObject();
        }

        return JsonDocument.Parse(buf.WrittenSpan.ToArray()).RootElement;
    }
}
