using System.IO;
using PacketReady.Application.Payers;
using PacketReady.Infrastructure.Payers;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Payers;

/// <summary>
/// Loader behavior is unit-tested against hand-written YAML in a temp dir so
/// the test stays decoupled from the committed payer files. A separate test
/// fixture rounds-trips the actual committed YAMLs to catch schema drift.
/// </summary>
public class PayerRequirementLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public PayerRequirementLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"payer-loader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Write(string filename, string contents)
    {
        var path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>
    /// Builds a syntactically valid payer YAML with overridable fields. Each
    /// shape-violation row below changes a single field, so the delta under
    /// test is obvious in the test body instead of buried in a copy-pasted
    /// block. Defaults match a minimal "payer-test" with board cert required.
    /// </summary>
    private static string YamlFor(
        string id = "payer-test",
        string name = "Payer Test",
        long perOccurrence = 1_000_000,
        long aggregate = 3_000_000,
        string[]? requiredDocuments = null,
        bool boardCertRequired = true,
        string[]? acceptedBoards = null,
        int malpracticeRenewal = 30,
        int licenseRenewal = 30)
    {
        requiredDocuments ??= ["license", "dea"];
        acceptedBoards ??= ["ABMS"];
        return $$"""
            id: {{id}}
            name: {{name}}
            malpractice:
              minimumPerOccurrence: {{perOccurrence}}
              minimumAggregate: {{aggregate}}
            requiredDocuments: [{{string.Join(", ", requiredDocuments)}}]
            boardCertRequired: {{(boardCertRequired ? "true" : "false")}}
            acceptedBoards: [{{string.Join(", ", acceptedBoards)}}]
            windowDays:
              malpracticeRenewal: {{malpracticeRenewal}}
              licenseRenewal: {{licenseRenewal}}
            """;
    }

    [Fact]
    public void LoadAll_ValidYaml_RoundTripsEveryField()
    {
        Write("payer-test.yaml", YamlFor());

        var loaded = PayerRequirementLoader.LoadAll(_tempDir);

        var p = Assert.Single(loaded.Values);
        Assert.Equal("payer-test", p.Id);
        Assert.Equal("Payer Test", p.Name);
        Assert.Equal(1_000_000, p.Malpractice.MinimumPerOccurrence);
        Assert.Equal(3_000_000, p.Malpractice.MinimumAggregate);
        Assert.Equal(new[] { "license", "dea" }, p.RequiredDocuments);
        Assert.True(p.BoardCertRequired);
        Assert.Equal(new[] { "ABMS" }, p.AcceptedBoards);
        Assert.Equal(30, p.WindowDays.MalpracticeRenewal);
        Assert.Equal(30, p.WindowDays.LicenseRenewal);
    }

    [Fact]
    public void LoadAll_KeyedById()
    {
        Write("payer-test.yaml", YamlFor());

        var loaded = PayerRequirementLoader.LoadAll(_tempDir);

        Assert.True(loaded.ContainsKey("payer-test"));
        Assert.False(loaded.ContainsKey("payer-test.yaml"));
    }

    [Fact]
    public void LoadAll_BoardCertOptionalWithEmptyAcceptedBoards_OK()
    {
        // Mirrors payer-b-state-medicaid.yaml: when board cert isn't required,
        // an empty acceptedBoards list is legal (the validator's accepted-boards
        // check never runs).
        Write("payer-optional.yaml", YamlFor(
            id: "payer-optional",
            boardCertRequired: false,
            acceptedBoards: []));

        var loaded = PayerRequirementLoader.LoadAll(_tempDir);

        var p = Assert.Single(loaded.Values);
        Assert.False(p.BoardCertRequired);
        Assert.Empty(p.AcceptedBoards);
    }

    public static IEnumerable<object[]> ShapeViolationCases()
    {
        // (expected-message fragment, YAML body). Filename is always
        // "payer-test.yaml" so stem-vs-id is satisfied except where the row
        // overrides `id` to break it deliberately.
        yield return ["file stem", YamlFor(id: "actually-this-id")];
        yield return ["'name' is required", YamlFor(name: "")];
        yield return ["requiredDocuments", YamlFor(requiredDocuments: [])];
        yield return ["boardCertRequired=true but acceptedBoards is empty",
                      YamlFor(boardCertRequired: true, acceptedBoards: [])];
        yield return ["boardCertRequired=false but acceptedBoards is non-empty",
                      YamlFor(boardCertRequired: false, acceptedBoards: ["ABMS"])];
        yield return ["malpractice.minimumPerOccurrence", YamlFor(perOccurrence: 0)];
        yield return ["must be >= minimumPerOccurrence",
                      YamlFor(perOccurrence: 2_000_000, aggregate: 1_000_000)];
        yield return ["windowDays.malpracticeRenewal", YamlFor(malpracticeRenewal: 0)];
        yield return ["windowDays.malpracticeRenewal", YamlFor(licenseRenewal: 0)];
    }

    [Theory]
    [MemberData(nameof(ShapeViolationCases))]
    public void LoadAll_ShapeViolations_ThrowWithFilenameAndReason(string expectedFragment, string yaml)
    {
        const string filename = "payer-test.yaml";
        Write(filename, yaml);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PayerRequirementLoader.LoadAll(_tempDir));
        Assert.Contains(expectedFragment, ex.Message);
        Assert.Contains(filename, ex.Message);
    }

    [Fact]
    public void LoadAll_TwoFilesWithSameId_StemCheckFiresBeforeDuplicateCheck()
    {
        // The duplicate-id branch in LoadAll is unreachable as long as the
        // stem-↔-id check holds (unique paths → unique stems → unique ids).
        // This test pins that ordering: two files claiming the same id will
        // trip the stem mismatch (one of them must have a stem != id) before
        // the dictionary insert ever sees a collision.
        Write("payer-a.yaml", YamlFor(id: "shared"));
        Write("payer-b.yaml", YamlFor(id: "shared"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => PayerRequirementLoader.LoadAll(_tempDir));
        Assert.Contains("file stem", ex.Message);
    }

    [Fact]
    public void LoadAll_MalformedYaml_ThrowsWithFilename()
    {
        Write("broken.yaml", "id: broken\nname: [unclosed");
        var ex = Assert.Throws<InvalidOperationException>(
            () => PayerRequirementLoader.LoadAll(_tempDir));
        Assert.Contains("broken.yaml", ex.Message);
        Assert.Contains("failed to parse", ex.Message);
    }

    [Fact]
    public void LoadAll_EmptyDirectory_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PayerRequirementLoader.LoadAll(_tempDir));
        Assert.Contains("no *.yaml files", ex.Message);
    }

    [Fact]
    public void LoadAll_MissingDirectory_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PayerRequirementLoader.LoadAll(Path.Combine(_tempDir, "does-not-exist")));
        Assert.Contains("does not exist", ex.Message);
    }
}

/// <summary>
/// Round-trip the actual committed YAMLs. If either payer file develops a
/// schema-shaped error, this fails — the regression net for "someone edited
/// the YAML on a Friday."
/// </summary>
public class CommittedPayerYamlTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, PayerRequirement>> Loaded =
        new(() => PayerRequirementLoader.LoadAll(
            Path.Combine(AppContext.BaseDirectory, "Payers", "payers")));

    [Fact]
    public void All_CommittedYamls_LoadCleanly()
    {
        // payer-a and payer-b are the production-style payers; payer-eval-seed
        // is the P4 orchestrator's no-PSV-required payer (slice 5+ fix).
        Assert.Equal(3, Loaded.Value.Count);
        Assert.True(Loaded.Value.ContainsKey("payer-a-national-hmo"));
        Assert.True(Loaded.Value.ContainsKey("payer-b-state-medicaid"));
        Assert.True(Loaded.Value.ContainsKey("payer-eval-seed"));
    }

    [Fact]
    public void PayerEvalSeed_HasRequiresSanctionsCheckFalse()
    {
        // Locked: slice 7 hinged on this. Flipping back to true here
        // re-introduces the "no sanctions on file" Critical on every
        // orchestrator-created provider — which masks 100% of validator
        // signals the eval is trying to measure. Production payers
        // (payer-a, payer-b) inherit the default-true via PayerRequirement
        // and stay strict.
        var s = Loaded.Value["payer-eval-seed"];
        Assert.False(s.RequiresSanctionsCheck);
        Assert.True(Loaded.Value["payer-a-national-hmo"].RequiresSanctionsCheck);
    }

    [Fact]
    public void PayerA_HasUniversal4_RequiredDocs()
    {
        var a = Loaded.Value["payer-a-national-hmo"];
        Assert.Equal(new[] { "license", "dea", "boardCert", "malpractice" }, a.RequiredDocuments);
        Assert.True(a.BoardCertRequired);
    }

    [Fact]
    public void PayerB_HasNoBoardCert_RequiredAndOptional()
    {
        // Exercises the boardCertRequired=false branch — the board-cert
        // validator extension reads this to suppress the missing-cert Critical.
        var b = Loaded.Value["payer-b-state-medicaid"];
        Assert.DoesNotContain("boardCert", b.RequiredDocuments);
        Assert.False(b.BoardCertRequired);
        Assert.Empty(b.AcceptedBoards);
    }
}
