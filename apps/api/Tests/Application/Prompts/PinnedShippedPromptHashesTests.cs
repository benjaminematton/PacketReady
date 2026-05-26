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
    private const string LicenseExtractionHash =
        "f80beae271a3c58eaec89b945b47062ce36fb06212c159d92f30d6afeb18cca3";
    private const string DeaExtractionHash =
        "e4323093b5eb57ea2d31c811d061ee594d4b27673474b52fdf32b14cc999ba59";
    private const string BoardCertExtractionHash =
        "683828d87da17ffb3b227339ca2126ca55da9e658d1510d7459aa332b9398bc4";
    private const string MalpracticeExtractionHash =
        "45b8b2a202ea0cf894bcdec44f36b7059d0e794cb57477f45db4a2b239cbe19f";
    // P4 task 8 — IdentityCoherenceValidator. Editing the prompt bumps to v2.md.
    private const string IdentityCoherenceHash =
        "d23dd984218bd73070153063067c982de2c589485fe3f6a2857abbb729b1c468";

    private static readonly IReadOnlyDictionary<string, string> Pinned =
        new Dictionary<string, string>
        {
            [PromptKeys.Classifier]            = ClassifierHash,
            [PromptKeys.LicenseExtraction]     = LicenseExtractionHash,
            [PromptKeys.DeaExtraction]         = DeaExtractionHash,
            [PromptKeys.BoardCertExtraction]   = BoardCertExtractionHash,
            [PromptKeys.MalpracticeExtraction] = MalpracticeExtractionHash,
            [PromptKeys.IdentityCoherence]     = IdentityCoherenceHash,
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
