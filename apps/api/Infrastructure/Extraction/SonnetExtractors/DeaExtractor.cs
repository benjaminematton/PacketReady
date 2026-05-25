using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// DEA Controlled Substances Registration extractor. Four string fields plus
/// <c>schedules</c> — a nullable string array drawn from
/// <c>["II", "III", "IV", "V"]</c> (Schedule I is not modeled: no accepted
/// medical use).
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

    protected override IReadOnlyList<FieldSpec> Fields { get; } = new FieldSpec[]
    {
        new("fullName",   FieldValueSchemas.NullableString),
        new("deaNumber",  FieldValueSchemas.NullableString),
        new("expiryDate", FieldValueSchemas.NullableString),
        new("status",     FieldValueSchemas.NullableString),
        new("schedules",  FieldValueSchemas.NullableStringEnumArray("II", "III", "IV", "V")),
    };
}
