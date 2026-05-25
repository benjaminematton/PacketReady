using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// Malpractice insurance certificate-of-coverage extractor. Five string fields;
/// expects two-page documents in the wild (carrier letterhead + the actual
/// certificate), so per-field <c>page</c> matters more here than for the
/// single-page License/DEA/BoardCert extractors.
/// </summary>
internal sealed class MalpracticeExtractor : SonnetExtractorBase
{
    public MalpracticeExtractor(
        IChatClient chat,
        IPromptLoader prompts,
        PromptHasher hasher,
        ILogger<MalpracticeExtractor> logger)
        : base(chat, prompts, hasher, logger)
    {
    }

    public override DocType DocType => DocType.Malpractice;
    public override string SchemaVersion => "malpractice.v1";
    public override string PromptResourceName => PromptKeys.MalpracticeExtraction;
    protected override string SchemaName => "malpractice_extraction";

    protected override string JsonSchema => """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["fields", "confidence"],
      "properties": {
        "fields": {
          "type": "object",
          "additionalProperties": false,
          "required": ["fullName", "carrier", "policyNumber", "expiryDate", "status"],
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
            "carrier": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "type": ["string", "null"] },
                "page":  { "type": "integer", "minimum": 1 },
                "bbox":  { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 }
              }
            },
            "policyNumber": {
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
          "required": ["fullName", "carrier", "policyNumber", "expiryDate", "status"],
          "properties": {
            "fullName":     { "type": "number", "minimum": 0, "maximum": 1 },
            "carrier":      { "type": "number", "minimum": 0, "maximum": 1 },
            "policyNumber": { "type": "number", "minimum": 0, "maximum": 1 },
            "expiryDate":   { "type": "number", "minimum": 0, "maximum": 1 },
            "status":       { "type": "number", "minimum": 0, "maximum": 1 }
          }
        }
      }
    }
    """;
}
