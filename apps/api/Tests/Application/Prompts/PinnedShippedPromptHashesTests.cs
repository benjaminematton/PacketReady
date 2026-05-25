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
        "62b8dd7331e38cb4fde7c78a92a1472c9ee3ca12240ab747aa9947cf54f1b0e1";
    private const string DeaExtractionHash =
        "f873ced7b1baece2c05e9aa51b6fafa9cbb5fd9bad33293b2c02cce7cdd95e4e";
    private const string BoardCertExtractionHash =
        "ad9c0db03b4c9b2226ba096c02a68f08a34f42286cfd46ce754514ecd28f19d5";
    private const string MalpracticeExtractionHash =
        "f34a452849da64ed047bd0e06926a7aeb9f4fb4582f563e7b5ab1631da232355";

    private static readonly IReadOnlyDictionary<string, string> Pinned =
        new Dictionary<string, string>
        {
            [PromptKeys.Classifier]            = ClassifierHash,
            [PromptKeys.LicenseExtraction]     = LicenseExtractionHash,
            [PromptKeys.DeaExtraction]         = DeaExtractionHash,
            [PromptKeys.BoardCertExtraction]   = BoardCertExtractionHash,
            [PromptKeys.MalpracticeExtraction] = MalpracticeExtractionHash,
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
