using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// Malpractice insurance certificate-of-coverage extractor. The certificates
/// are routinely two pages (carrier letterhead + the actual coverage page),
/// so per-field <c>page</c> matters more here than for the single-page
/// License/DEA/BoardCert extractors.
///
/// <para><b>v2 (P4):</b> adds two integer coverage-limit fields,
/// <c>perOccurrence</c> and <c>aggregate</c> (whole dollars), consumed by the
/// <c>MalpracticeCurrencyValidator</c> Major-below-minimum branch. Both stay
/// nullable — some certificates print only one of the two — and the validator
/// treats <c>null</c> as "extractor couldn't read it", which is the
/// aggregator's Partial-Extraction lane, not this validator's.</para>
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
    public override string SchemaVersion => "malpractice.v2";
    public override string PromptResourceName => PromptKeys.MalpracticeExtraction;
    protected override string SchemaName => "malpractice_extraction";

    protected override IReadOnlyList<FieldSpec> Fields { get; } = new FieldSpec[]
    {
        new("fullName",      FieldValueSchemas.NullableString),
        new("carrier",       FieldValueSchemas.NullableString),
        new("policyNumber",  FieldValueSchemas.NullableString),
        new("expiryDate",    FieldValueSchemas.NullableString),
        new("status",        FieldValueSchemas.NullableString),
        new("perOccurrence", FieldValueSchemas.NullableInteger),
        new("aggregate",     FieldValueSchemas.NullableInteger),
    };
}
