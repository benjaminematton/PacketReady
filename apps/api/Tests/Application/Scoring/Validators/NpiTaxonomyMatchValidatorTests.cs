using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Nucc;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using PacketReady.Tests.Application.Prompts;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Scoring.Validators;

/// <summary>
/// Wire-shape coverage. We don't hit Sonnet — the LLM-side decision is
/// exercised in the eval pipeline. Pins:
///   - short-circuit when license/boardCert/taxonomyCode missing,
///   - NUCC lookup miss → no Issue (no signal to act on),
///   - LLM returns matches=true → no Issue,
///   - LLM returns matches=false → one Critical with Field="boardCert.specialty",
///   - malformed LLM JSON → empty (no fabricated Criticals),
///   - citations resolve from the provenance map for both fields.
/// </summary>
public class NpiTaxonomyMatchValidatorTests
{
    private const string FakePrompt = "You are a test prompt.";

    // 207R00000X is a stable code; use a fake mapping in tests so they don't
    // couple to the shipped NUCC snapshot's Display Name string drift.
    private const string KnownCode = "207R00000X";
    private const string KnownCanonical = "Internal Medicine Physician";

    [Fact]
    public async Task ShortCircuits_WhenLicenseOrBoardCertMissing()
    {
        var v = Build(new RecordingChatClient(), StubLookup(matches: true));

        var noLicense = MakeProfile() with { License = null };
        Assert.Empty(await v.RunAsync(noLicense, EmptyProvenance(), Provider.DefaultPayerId, default));

        var noBoardCert = MakeProfile() with { BoardCert = null };
        Assert.Empty(await v.RunAsync(noBoardCert, EmptyProvenance(), Provider.DefaultPayerId, default));
    }

    [Fact]
    public async Task ShortCircuits_WhenLicenseHasNoTaxonomyCode()
    {
        // Pre-v2 license rows simply lack the code; the validator stays silent.
        var v = Build(new RecordingChatClient(), StubLookup(matches: true));
        var profile = MakeProfile(license: MakeLicense() with { TaxonomyCode = "" });

        var issues = await v.RunAsync(profile, EmptyProvenance(), Provider.DefaultPayerId, default);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task NuccLookupMiss_ReturnsEmpty()
    {
        // Unknown taxonomy code → "no signal" not "fabricated Critical".
        var chat = new RecordingChatClient();
        var lookup = new StubNuccLookup();   // empty dict
        var v = Build(chat, lookup);
        var profile = MakeProfile(license: MakeLicense() with { TaxonomyCode = "999XYZ0000X" });

        var issues = await v.RunAsync(profile, EmptyProvenance(), Provider.DefaultPayerId, default);

        Assert.Empty(issues);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task LlmMatches_ReturnsEmpty()
    {
        var chat = ChatReturning("""{"matches": true, "suggestedFix": null}""");
        var v = Build(chat, StubLookupKnown());
        var profile = MakeProfile(license: MakeLicense() with { TaxonomyCode = KnownCode });

        var issues = await v.RunAsync(profile, EmptyProvenance(), Provider.DefaultPayerId, default);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task LlmMismatch_EmitsCriticalWithFieldDiscriminator()
    {
        var chat = ChatReturning("""{"matches": false, "suggestedFix": "Internal Medicine"}""");
        var v = Build(chat, StubLookupKnown());
        var profile = MakeProfile(
            license:   MakeLicense()   with { TaxonomyCode = KnownCode },
            boardCert: MakeBoardCert() with { Specialty = "Family Medicine" });

        var issues = await v.RunAsync(
            profile,
            ProvenanceFor("license.taxonomyCode", "boardCert.specialty"),
            Provider.DefaultPayerId, default);

        var only = Assert.Single(issues);
        Assert.Equal("npi_taxonomy_match", only.Validator);
        Assert.Equal(Severity.Critical, only.Severity);
        // 3-predicate match contract: the runner reads Field to distinguish
        // a taxonomy-mismatch catch from an off-target identity finding.
        Assert.Equal("boardCert.specialty", only.Field);
        Assert.Contains(KnownCanonical, only.Message);
        Assert.Contains("Family Medicine", only.Message);
        Assert.Contains("Internal Medicine", only.Remediation);
        Assert.Equal(2, only.Citations.Count);
        Assert.All(only.Citations, c => Assert.NotNull(c.DocumentId));
    }

    [Fact]
    public async Task MalformedJsonResponse_ReturnsEmpty_DoesNotThrow()
    {
        var chat = ChatReturning("not json at all");
        var v = Build(chat, StubLookupKnown());
        var profile = MakeProfile(license: MakeLicense() with { TaxonomyCode = KnownCode });

        Assert.Empty(await v.RunAsync(profile, EmptyProvenance(), Provider.DefaultPayerId, default));
    }

    // === helpers ============================================================

    private static NpiTaxonomyMatchValidator Build(IChatClient chat, INuccTaxonomyLookup lookup)
    {
        var prompts = StubPromptLoaderFactory.Create("NpiTaxonomyMatchPrompt.v1.md", FakePrompt);
        return new NpiTaxonomyMatchValidator(
            chat, prompts, lookup, NullLogger<NpiTaxonomyMatchValidator>.Instance);
    }

    private static StubNuccLookup StubLookup(bool matches) =>
        matches ? StubLookupKnown() : new StubNuccLookup();

    private static StubNuccLookup StubLookupKnown() =>
        new(new Dictionary<string, string> { [KnownCode] = KnownCanonical });

    private static IReadOnlyDictionary<string, FieldProvenance> EmptyProvenance() =>
        new Dictionary<string, FieldProvenance>();

    private static IReadOnlyDictionary<string, FieldProvenance> ProvenanceFor(params string[] keys)
    {
        var map = new Dictionary<string, FieldProvenance>();
        foreach (var k in keys)
            map[k] = new FieldProvenance(
                DocumentId: Guid.NewGuid(),
                Page: 1,
                Bbox: new BoundingBox(0, 0, 100, 20),
                Confidence: 0.99);
        return map;
    }

    private static RecordingChatClient ChatReturning(string responseJson)
    {
        var msg = new ChatMessage(ChatRole.Assistant, [new TextContent(responseJson)]);
        return new RecordingChatClient(new ChatResponse(msg));
    }

    private sealed class StubNuccLookup : INuccTaxonomyLookup
    {
        private readonly IReadOnlyDictionary<string, string> _table;
        public StubNuccLookup(IReadOnlyDictionary<string, string>? table = null)
        {
            _table = table ?? new Dictionary<string, string>();
        }

        public bool TryGet(string code, out string canonicalSpecialty)
        {
            if (_table.TryGetValue(code, out var v))
            {
                canonicalSpecialty = v;
                return true;
            }
            canonicalSpecialty = "";
            return false;
        }

        public int Count => _table.Count;
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ChatResponse _response;
        public List<ChatOptions?> Calls { get; } = new();

        public RecordingChatClient(ChatResponse? response = null)
        {
            _response = response ?? new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [new TextContent("")]));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(options);
            return Task.FromResult(_response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
