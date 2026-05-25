using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// DEA Controlled Substances Registration extractor. Five fields including
/// <c>schedules</c> — a string array (subset of <c>["II", "III", "IV", "V"]</c>),
/// the only array-valued field across the four P3 extractors. The schema below
/// accepts <c>null</c> or an array of schedule strings; <c>SonnetExtractorBase.SplitLlmResponse</c>
/// passes the array through to <c>FieldsJson</c> unchanged.
/// </summary>
internal sealed class DeaExtractor : SonnetExtractorBase
{
    public DeaExtractor(
        IChatClient chat,
        IPromptLoader prompts,
        PromptHasher hasher,
        ILogger<DeaExtractor> logger)
        : base(chat, prompts, hasher, logger)
    {
    }

    public override DocType DocType => DocType.Dea;
    public override string SchemaVersion => "dea.v1";
    public override string PromptResourceName => PromptKeys.DeaExtraction;
    protected override string SchemaName => "dea_extraction";

    // Inline schema. The four string fields use the same envelope as
    // LicenseExtractor; `schedules` has an array `value` type. Schedule I is
    // not modeled (no accepted medical use), so the enum list is II–V.
    protected override string JsonSchema => """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["fields", "confidence"],
      "properties": {
        "fields": {
          "type": "object",
          "additionalProperties": false,
          "required": ["fullName", "deaNumber", "expiryDate", "status", "schedules"],
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
            "deaNumber": {
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
            },
            "schedules": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": {
                  "type": ["array", "null"],
                  "items": { "type": "string", "enum": ["II", "III", "IV", "V"] }
                },
                "page":  { "type": "integer", "minimum": 1 },
                "bbox":  { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 }
              }
            }
          }
        },
        "confidence": {
          "type": "object",
          "additionalProperties": false,
          "required": ["fullName", "deaNumber", "expiryDate", "status", "schedules"],
          "properties": {
            "fullName":   { "type": "number", "minimum": 0, "maximum": 1 },
            "deaNumber":  { "type": "number", "minimum": 0, "maximum": 1 },
            "expiryDate": { "type": "number", "minimum": 0, "maximum": 1 },
            "status":     { "type": "number", "minimum": 0, "maximum": 1 },
            "schedules":  { "type": "number", "minimum": 0, "maximum": 1 }
          }
        }
      }
    }
    """;
}
