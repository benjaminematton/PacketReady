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
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            },
            "carrier": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            },
            "policyNumber": {
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
          "required": ["fullName", "carrier", "policyNumber", "expiryDate", "status"],
          "properties": {
            "fullName":     { "type": "number" },
            "carrier":      { "type": "number" },
            "policyNumber": { "type": "number" },
            "expiryDate":   { "type": "number" },
            "status":       { "type": "number" }
          }
        }
      }
    }
    """;
}
