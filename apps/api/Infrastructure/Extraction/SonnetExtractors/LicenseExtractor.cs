using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// State medical license extractor. Schema mirrors the spec's locked sample at
/// docs/impl/phase-3-extractors.md §"Extractor output shape"; the field list and
/// confidence keys must stay in sync with the prompt at
/// <c>LicenseExtractionPrompt.v1.md</c> — golden-truth at
/// <c>evals/dataset/packet-001-clean-anderson/golden.json</c> pins both.
/// </summary>
internal sealed class LicenseExtractor : SonnetExtractorBase
{
    public LicenseExtractor(
        IChatClient chat,
        IPromptLoader prompts,
        PromptHasher hasher,
        ILogger<LicenseExtractor> logger)
        : base(chat, prompts, hasher, logger)
    {
    }

    public override DocType DocType => DocType.License;
    public override string SchemaVersion => "license.v1";
    public override string PromptResourceName => PromptKeys.LicenseExtraction;
    protected override string SchemaName => "license_extraction";

    // Inline (not $ref/$defs) — some structured-output adapters don't expand
    // $defs reliably. Six fields are tractable to spell out; if we ever grow
    // past ~15, switch to a schema builder.
    protected override string JsonSchema => """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["fields", "confidence"],
      "properties": {
        "fields": {
          "type": "object",
          "additionalProperties": false,
          "required": ["fullName", "licenseNumber", "state", "issueDate", "expiryDate", "status"],
          "properties": {
            "fullName": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "type": ["string", "null"] },
                "page":  { "type": "integer", "minimum": 1 },
                "bbox":  { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 }
              }
            },
            "licenseNumber": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "type": ["string", "null"] },
                "page":  { "type": "integer", "minimum": 1 },
                "bbox":  { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 }
              }
            },
            "state": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "type": ["string", "null"] },
                "page":  { "type": "integer", "minimum": 1 },
                "bbox":  { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 }
              }
            },
            "issueDate": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "type": ["string", "null"] },
                "page":  { "type": "integer", "minimum": 1 },
                "bbox":  { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 }
              }
            },
            "expiryDate": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "type": ["string", "null"] },
                "page":  { "type": "integer", "minimum": 1 },
                "bbox":  { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 }
              }
            },
            "status": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "type": ["string", "null"] },
                "page":  { "type": "integer", "minimum": 1 },
                "bbox":  { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 }
              }
            }
          }
        },
        "confidence": {
          "type": "object",
          "additionalProperties": false,
          "required": ["fullName", "licenseNumber", "state", "issueDate", "expiryDate", "status"],
          "properties": {
            "fullName":      { "type": "number", "minimum": 0, "maximum": 1 },
            "licenseNumber": { "type": "number", "minimum": 0, "maximum": 1 },
            "state":         { "type": "number", "minimum": 0, "maximum": 1 },
            "issueDate":     { "type": "number", "minimum": 0, "maximum": 1 },
            "expiryDate":    { "type": "number", "minimum": 0, "maximum": 1 },
            "status":        { "type": "number", "minimum": 0, "maximum": 1 }
          }
        }
      }
    }
    """;
}
