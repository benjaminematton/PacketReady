using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// ABMS member-board certification extractor. Six string fields; the doc-specific
/// contract (board acronym normalization, subspecialty handling) lives in
/// <c>BoardCertExtractionPrompt.v1.md</c>.
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

    protected override IReadOnlyList<FieldSpec> Fields { get; } = new FieldSpec[]
    {
        new("fullName",   FieldValueSchemas.NullableString),
        new("board",      FieldValueSchemas.NullableString),
        new("specialty",  FieldValueSchemas.NullableString),
        new("issueDate",  FieldValueSchemas.NullableString),
        new("expiryDate", FieldValueSchemas.NullableString),
        new("status",     FieldValueSchemas.NullableString),
    };
}
