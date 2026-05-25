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
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            },
            "licenseNumber": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            },
            "state": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            },
            "issueDate": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            },
            "expiryDate": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            },
            "status": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            }
          }
        },
        "confidence": {
          "type": "object",
          "additionalProperties": false,
          "required": ["fullName", "licenseNumber", "state", "issueDate", "expiryDate", "status"],
          "properties": {
            "fullName":      { "type": "number" },
            "licenseNumber": { "type": "number" },
            "state":         { "type": "number" },
            "issueDate":     { "type": "number" },
            "expiryDate":    { "type": "number" },
            "status":        { "type": "number" }
          }
        }
      }
    }
    """;
}
