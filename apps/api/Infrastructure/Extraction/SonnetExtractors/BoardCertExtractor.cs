using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// ABMS member-board certification extractor. Six string fields, identical
/// envelope shape to <see cref="LicenseExtractor"/>; the doc-specific contract
/// lives in the prompt (board acronym normalization, subspecialty handling).
/// </summary>
internal sealed class BoardCertExtractor : SonnetExtractorBase
{
    public BoardCertExtractor(
        IChatClient chat,
        IPromptLoader prompts,
        PromptHasher hasher,
        ILogger<BoardCertExtractor> logger)
        : base(chat, prompts, hasher, logger)
    {
    }

    public override DocType DocType => DocType.BoardCert;
    public override string SchemaVersion => "boardCert.v1";
    public override string PromptResourceName => PromptKeys.BoardCertExtraction;
    protected override string SchemaName => "board_cert_extraction";

    protected override string JsonSchema => """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["fields", "confidence"],
      "properties": {
        "fields": {
          "type": "object",
          "additionalProperties": false,
          "required": ["fullName", "board", "specialty", "issueDate", "expiryDate", "status"],
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
            "board": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value", "page", "bbox"],
              "properties": {
                "value": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
                "page":  { "type": "integer" },
                "bbox":  { "type": "array", "items": { "type": "number" } }
              }
            },
            "specialty": {
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
          "required": ["fullName", "board", "specialty", "issueDate", "expiryDate", "status"],
          "properties": {
            "fullName":   { "type": "number" },
            "board":      { "type": "number" },
            "specialty":  { "type": "number" },
            "issueDate":  { "type": "number" },
            "expiryDate": { "type": "number" },
            "status":     { "type": "number" }
          }
        }
      }
    }
    """;
}
