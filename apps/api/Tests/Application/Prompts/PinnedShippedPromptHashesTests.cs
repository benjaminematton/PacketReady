using PacketReady.Application.Prompts;
using Xunit;

namespace PacketReady.Tests.Application.Prompts;

/// <summary>
/// Pinned SHA-256 per shipped prompt. The hash lands on every
/// <c>document_extractions.prompt_hash</c> column produced by that prompt's
/// extractor — once an extraction row exists referencing it, the prompt is
/// effectively frozen at this byte sequence (spec §"Idempotency cache poisoning
/// by silent prompt edit").
///
/// <para><b>How to evolve:</b> a prompt edit is a <c>v2.md</c> file, not a tweak
/// to <c>v1.md</c>. This test failing means somebody edited a v1 — either revert
/// and promote to v2, or update the constants here only after migrating all
/// existing rows that referenced the old hash.</para>
///
/// <para>This is a stronger check than <c>ShippedPromptsResolveTests.EveryShippedPromptHashes…</c>
/// (which only asserted determinism across two calls). Hash stability across
/// commits — the actual audit-trail invariant — lives here.</para>
/// </summary>
public class PinnedShippedPromptHashesTests
{
    // SHA-256, lowercase hex. Computed from the on-disk LF-normalized prompt
    // bytes (pinned via .gitattributes). Regenerate with:
    //   shasum -a 256 apps/api/Application/Extraction/Prompts/*.md
    private const string ClassifierHash =
        "cee7814eab693d8fe6cc3b546ecf7bb53ce5781c5821688aa0dfa6e83c2daf91";
    // Bumped to v2 in P4 task 10 — added taxonomyCode field consumed by
    // NpiTaxonomyMatchValidator. Same shape pattern as malpractice v2.
    private const string LicenseExtractionHash =
        "ace0e12dcf86522af6838a559496358da1d3ae2485045189b322ddbd7e9d26c2";
    private const string DeaExtractionHash =
        "e4323093b5eb57ea2d31c811d061ee594d4b27673474b52fdf32b14cc999ba59";
    private const string BoardCertExtractionHash =
        "683828d87da17ffb3b227339ca2126ca55da9e658d1510d7459aa332b9398bc4";
    // Bumped to v2 in P4 task 11 — added integer perOccurrence / aggregate
    // coverage-limit fields consumed by MalpracticeCurrencyValidator. v1 had
    // five string fields; v2 has the same five plus the two integers, with
    // matching schema/extractor/aggregator updates. Old extraction rows
    // pinned at the v1 hash remain valid; the column carries the prompt
    // version that produced them.
    private const string MalpracticeExtractionHash =
        "a00ff1ee956fb3892a663834a8ba71f88995ade18229be8c43462c323ce11fe7";
    // P4 task 8 — IdentityCoherenceValidator. Editing the prompt bumps to v2.md.
    // Re-pinned during task 9 iteration loop. The IdentityCoherence prompt is
    // actively tuned in this phase; each iteration's hash lands here once
    // converged. The validator's hash isn't referenced by any document_extractions
    // row (validators don't produce extractions), so per-iteration churn here is
    // safe — the audit-trail "promote to v2.md" rule only applies to extractor
    // prompts. Iter 1: surname_typo_overreact rule strengthened.
    private const string IdentityCoherenceHash =
        "48322ce74b3586a737a2b2f56e516154103b557f4c18edb647d1e703f92943a4";
    // P4 task 10 — NpiTaxonomyMatch v1. Tuned alongside task 9's
    // IdentityCoherence; same FP < 5% gate on the 10-packet subset.
    private const string NpiTaxonomyMatchHash =
        "8efaa96de84674dd455031270f50bd3246aed6b5a4a026f5756ce301c4d352a9";
    // P5 C4 — intake agent system prompt. The agent prompt isn't
    // referenced by any document_extractions row (the agent doesn't
    // produce extractions), so per-iteration churn here is safe — the
    // promote-to-v2 rule applies to extractor prompts. Pinning anyway
    // keeps every shipped prompt under the same immutability gate so a
    // silent edit can't slip through code review.
    private const string IntakeAgentHash =
        "9a131e99ac1be653d25fb706e290bd044c2bc252ff62331324c43dd63b4e17d5";

    private static readonly IReadOnlyDictionary<string, string> Pinned =
        new Dictionary<string, string>
        {
            [PromptKeys.Classifier]            = ClassifierHash,
            [PromptKeys.LicenseExtraction]     = LicenseExtractionHash,
            [PromptKeys.DeaExtraction]         = DeaExtractionHash,
            [PromptKeys.BoardCertExtraction]   = BoardCertExtractionHash,
            [PromptKeys.MalpracticeExtraction] = MalpracticeExtractionHash,
            [PromptKeys.IdentityCoherence]     = IdentityCoherenceHash,
            [PromptKeys.NpiTaxonomyMatch]      = NpiTaxonomyMatchHash,
            [PromptKeys.IntakeAgent]           = IntakeAgentHash,
        };

    public static IEnumerable<object[]> PinnedPromptHashes =>
        Pinned.Select(kv => new object[] { kv.Key, kv.Value });

    [Theory]
    [MemberData(nameof(PinnedPromptHashes))]
    public async Task ShippedPromptHashMatchesPinnedValue(string promptKey, string expectedHash)
    {
        var hasher = new PromptHasher(new PromptLoader());

        var actual = await hasher.HashOfAsync(promptKey, CancellationToken.None);

        Assert.Equal(expectedHash, actual);
    }

    [Fact]
    public void EveryShippedPromptKeyHasAPinnedHash()
    {
        // Reflective pairing: every public const string on PromptKeys must
        // appear in the pinned dictionary above. Adding a new PromptKeys.*
        // constant without pinning its hash fails here instead of silently
        // bypassing the immutability check.
        var declaredKeys = typeof(PromptKeys)
            .GetFields(System.Reflection.BindingFlags.Public
                       | System.Reflection.BindingFlags.Static
                       | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Equal(declaredKeys, Pinned.Keys.ToHashSet());
    }
}
