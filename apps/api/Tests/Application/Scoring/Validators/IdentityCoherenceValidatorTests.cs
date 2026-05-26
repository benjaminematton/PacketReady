using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using PacketReady.Tests.Application.Prompts;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Scoring.Validators;

/// <summary>
/// Wire-shape coverage for IdentityCoherenceValidator. We don't hit Sonnet — the
/// real LLM call is exercised in the eval-runner pipeline (task 9). These tests
/// pin:
///   - short-circuit when fewer than 2 doc sources have a per-doc fullName,
///   - structured-output parsing (FunctionCallContent path) → Issue list,
///   - empty/garbage response → empty Issue list (no fabricated Criticals),
///   - severity parsing maps the schema enum to Severity correctly,
///   - citations resolve from the provenance map per source docType.
/// </summary>
public class IdentityCoherenceValidatorTests
{
    private const string FakePrompt = "You are a test prompt.";

    [Fact]
    public async Task ShortCircuits_WhenFewerThanTwoSourceNames()
    {
        // Only license carries a fullName; can't conflict with self.
        var profile = MakeProfile(
            license: MakeLicense() with { FullName = "Solo Soloist, MD" },
            dea: MakeDea() with { FullName = "" },
            boardCert: MakeBoardCert() with { FullName = "" });
        var chat = new RecordingChatClient();
        var v = Build(chat);

        var issues = await v.RunAsync(profile, EmptyProvenance(), Provider.DefaultPayerId, default);

        Assert.Empty(issues);
        Assert.Empty(chat.Calls);  // never went to the wire
    }

    [Fact]
    public async Task NoExtractedNames_ReturnsEmpty()
    {
        // Every doc sub-record is null — aggregator owns Missing-Document Criticals.
        var profile = MakeProfile() with { License = null, Dea = null, BoardCert = null };
        var chat = new RecordingChatClient();
        var v = Build(chat);

        Assert.Empty(await v.RunAsync(profile, EmptyProvenance(), Provider.DefaultPayerId, default));
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task EmptyDisagreements_ReturnsEmpty()
    {
        var chat = ChatReturning("""{"disagreements":[]}""");
        var v = Build(chat);

        var issues = await v.RunAsync(ProfileWithThreeMatchingNames(), EmptyProvenance(), Provider.DefaultPayerId, default);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task ParsesSingleDisagreement_EmitsCriticalWithCitations()
    {
        var chat = ChatReturning("""
            {
              "disagreements": [
                {
                  "field": "fullName",
                  "severity": "Critical",
                  "message": "License records 'Henry Anderson, MD'; board cert records 'Henrietta Bautista'.",
                  "remediation": "Confirm legal name.",
                  "sources": ["license", "boardCert"]
                }
              ]
            }
            """);
        var v = Build(chat);

        var issues = await v.RunAsync(
            ProfileWithDistinguishableNames(),
            ProvenanceFor("license", "boardCert"),
            Provider.DefaultPayerId,
            default);

        var only = Assert.Single(issues);
        Assert.Equal("identity_coherence", only.Validator);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Equal(2, only.Citations.Count);
        // ExtractedValue is the per-doc fullName string, not the docType
        // label — the dashboard chip needs to render the actual conflicting
        // value, and pre-fix this carried "license"/"boardCert".
        Assert.Contains(only.Citations,
            c => c.ExtractedValue == "Henry Anderson, MD" && c.DocumentId is not null);
        Assert.Contains(only.Citations,
            c => c.ExtractedValue == "Henrietta Bautista" && c.DocumentId is not null);
    }

    [Theory]
    [InlineData("Critical", Severity.Critical)]
    [InlineData("Minor",    Severity.Minor)]
    // Schema enum is locked to {Critical, Minor}; any other value is the LLM
    // going off-schema and should downgrade to Minor (safe default).
    [InlineData("Major",    Severity.Minor)]
    [InlineData("Severe",   Severity.Minor)]
    public async Task SeverityParse_HandlesEnumAndOffSchema(string severityFromModel, Severity expected)
    {
        var chat = ChatReturning($$"""
            {"disagreements":[{"field":"fullName","severity":"{{severityFromModel}}","message":"m","remediation":"r","sources":["license","dea"]}]}
            """);
        var v = Build(chat);

        var issues = await v.RunAsync(ProfileWithThreeMatchingNames(), EmptyProvenance(), Provider.DefaultPayerId, default);

        Assert.Equal(expected, Assert.Single(issues).Severity);
    }

    [Fact]
    public async Task MalformedJsonResponse_ReturnsEmpty_DoesNotThrow()
    {
        // Adapter drift or an empty response shouldn't fabricate Criticals;
        // validator logs and returns empty.
        var chat = ChatReturning("not valid json at all");
        var v = Build(chat);

        Assert.Empty(await v.RunAsync(ProfileWithThreeMatchingNames(), EmptyProvenance(), Provider.DefaultPayerId, default));
    }

    // === helpers ==========================================================

    private static IdentityCoherenceValidator Build(IChatClient chat)
    {
        var prompts = StubPromptLoaderFactory.Create("IdentityCoherencePrompt.v1.md", FakePrompt);
        return new IdentityCoherenceValidator(
            chat,
            prompts,
            NullLogger<IdentityCoherenceValidator>.Instance);
    }

    private static ProviderProfile ProfileWithThreeMatchingNames() => MakeProfile(
        license:   MakeLicense()   with { FullName = "Henry Anderson, MD" },
        dea:       MakeDea()       with { FullName = "Henry Anderson" },
        boardCert: MakeBoardCert() with { FullName = "Henry Anderson, MD" });

    // Distinguishable per-doc names so a citation-assertion can prove the
    // ExtractedValue matches the source it was cited against.
    private static ProviderProfile ProfileWithDistinguishableNames() => MakeProfile(
        license:   MakeLicense()   with { FullName = "Henry Anderson, MD" },
        dea:       MakeDea()       with { FullName = "Henry Anderson" },
        boardCert: MakeBoardCert() with { FullName = "Henrietta Bautista" });

    private static IReadOnlyDictionary<string, FieldProvenance> EmptyProvenance() =>
        new Dictionary<string, FieldProvenance>();

    private static IReadOnlyDictionary<string, FieldProvenance> ProvenanceFor(params string[] docTypes)
    {
        var map = new Dictionary<string, FieldProvenance>();
        foreach (var dt in docTypes)
            map[$"{dt}.fullName"] = new FieldProvenance(
                DocumentId: Guid.NewGuid(),
                Page: 1,
                Bbox: new BoundingBox(0, 0, 100, 20),
                Confidence: 0.99);
        return map;
    }

    private static RecordingChatClient ChatReturning(string responseJson)
    {
        var msg = new ChatMessage(ChatRole.Assistant, [new TextContent(responseJson)]);
        var resp = new ChatResponse(msg);
        return new RecordingChatClient(resp);
    }

    /// <summary>Minimal IChatClient that records the call and returns a fixed
    /// response. Avoids pulling Moq into a tiny dependency surface.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ChatResponse _response;
        public List<ChatOptions?> Calls { get; } = new();

        public RecordingChatClient(ChatResponse? response = null)
        {
            _response = response ?? new ChatResponse(new ChatMessage(
                ChatRole.Assistant, [new TextContent("")]));
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
