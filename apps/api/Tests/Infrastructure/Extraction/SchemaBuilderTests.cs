using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Infrastructure.Extraction.SonnetExtractors;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Extraction;

/// <summary>
/// Coverage for <c>SonnetExtractorBase.BuildSchemaJson</c>. The schema fed to
/// <c>ChatResponseFormat.ForJsonSchema</c> must stay inside the intersection of
/// what <c>Anthropic.SDK</c>'s preprocessor and Anthropic's server-side
/// validator accept (see <c>docs/impl/phase-3-extractors.md</c> §"Anthropic
/// structured-output schema subset"). These tests pin that:
///
///   - the generated text parses as JSON,
///   - <c>type</c> values are strings (not arrays — type-array unions get
///     rejected by the SDK preprocessor),
///   - the banned numeric-range / cardinality keywords don't appear,
///   - every declared field shows up in both <c>fields.required</c> and
///     <c>confidence.required</c>.
///
/// We don't snapshot the full schema string — a snapshot would lock cosmetic
/// formatting (whitespace, key order) we don't care about. The structural
/// invariants are what matter.
/// </summary>
public class SchemaBuilderTests
{
    /// <summary>The structural keywords Anthropic's validator rejects.</summary>
    private static readonly string[] BannedKeywords =
    {
        "\"minimum\"", "\"maximum\"", "\"minItems\"", "\"maxItems\"",
        "\"exclusiveMinimum\"", "\"exclusiveMaximum\"",
    };

    // Parameters are typed `object` because SonnetExtractorBase is internal in
    // the Infrastructure assembly; the test class must stay public per xUnit's
    // analyzer (xUnit1000), and a public method can't expose an internal type.
    public static IEnumerable<object[]> AllExtractors()
    {
        yield return new object[] { new LicenseExtractor(
            null!, null!, null!, NullLogger<LicenseExtractor>.Instance) };
        yield return new object[] { new DeaExtractor(
            null!, null!, null!, NullLogger<DeaExtractor>.Instance) };
        yield return new object[] { new BoardCertExtractor(
            null!, null!, null!, NullLogger<BoardCertExtractor>.Instance) };
        yield return new object[] { new MalpracticeExtractor(
            null!, null!, null!, NullLogger<MalpracticeExtractor>.Instance) };
    }

    [Theory]
    [MemberData(nameof(AllExtractors))]
    public void Schema_IsValidJson(object extractorObj)
    {
        var extractor = (SonnetExtractorBase)extractorObj;
        var schemaJson = extractor.BuildSchemaJson();

        using var doc = JsonDocument.Parse(schemaJson);

        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.True(root.GetProperty("properties").TryGetProperty("fields", out _));
        Assert.True(root.GetProperty("properties").TryGetProperty("confidence", out _));
    }

    [Theory]
    [MemberData(nameof(AllExtractors))]
    public void Schema_AvoidsAnthropicBannedKeywords(object extractorObj)
    {
        var extractor = (SonnetExtractorBase)extractorObj;
        var schemaJson = extractor.BuildSchemaJson();

        foreach (var banned in BannedKeywords)
        {
            Assert.DoesNotContain(banned, schemaJson);
        }
    }

    [Theory]
    [MemberData(nameof(AllExtractors))]
    public void Schema_DoesNotUseTypeArrayUnions(object extractorObj)
    {
        // Anthropic.SDK's EnsureAdditionalPropertiesFalse preprocessor walks
        // every "type" node and calls GetValue<string>() — it throws if any
        // "type" is an array. The nullability pattern must go through anyOf.
        var extractor = (SonnetExtractorBase)extractorObj;
        var schemaJson = extractor.BuildSchemaJson();
        using var doc = JsonDocument.Parse(schemaJson);

        AssertNoTypeArrays(doc.RootElement);
    }

    [Theory]
    [MemberData(nameof(AllExtractors))]
    public void Schema_RequiredArraysMatchOnFieldsAndConfidence(object extractorObj)
    {
        var extractor = (SonnetExtractorBase)extractorObj;
        // The wrapping schema requires identical key sets in fields.* and
        // confidence.* — if these ever diverge, the LLM gets a contradictory
        // shape contract. This test catches accidental drift in the builder.
        var schemaJson = extractor.BuildSchemaJson();
        using var doc = JsonDocument.Parse(schemaJson);

        var props = doc.RootElement.GetProperty("properties");
        var fieldsRequired = props.GetProperty("fields").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToHashSet();
        var confidenceRequired = props.GetProperty("confidence").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToHashSet();

        Assert.Equal(fieldsRequired, confidenceRequired);
        Assert.NotEmpty(fieldsRequired);
    }

    private static void AssertNoTypeArrays(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("type") && prop.Value.ValueKind == JsonValueKind.Array)
                        Assert.Fail($"Found type-array union: {prop.Value.GetRawText()} — use anyOf instead.");
                    AssertNoTypeArrays(prop.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AssertNoTypeArrays(item);
                break;
        }
    }
}
