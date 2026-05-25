using PacketReady.Domain.Documents;
using Xunit;

namespace PacketReady.Tests.Domain.Documents;

public class DocumentExtractionTests
{
    private const string Fields = """{"fullName":{"value":"Anderson","page":1,"bbox":[1,2,3,4]}}""";
    private const string Locations = """{"fullName":{"page":1,"bbox":[1,2,3,4]}}""";
    private const string Confidence = """{"fullName":0.97}""";

    private static DocumentExtraction Succeeded() =>
        DocumentExtraction.CreateLlmSucceeded(
            documentId: Guid.NewGuid(),
            extractionId: 1,
            schemaVersion: "license.v1",
            fieldsJson: Fields,
            fieldLocationsJson: Locations,
            confidenceJson: Confidence,
            model: "claude-sonnet-4-6",
            promptHash: new string('a', 64),
            inputTokens: 5000,
            outputTokens: 400);

    private static DocumentExtraction Failed() =>
        DocumentExtraction.CreateLlmFailed(
            documentId: Guid.NewGuid(),
            extractionId: 1,
            schemaVersion: "license.v1",
            error: "structured-output parse failed",
            model: "claude-sonnet-4-6",
            promptHash: new string('b', 64));

    [Fact]
    public void Succeeded_PopulatesAllProvenance()
    {
        var e = Succeeded();

        Assert.Equal(ExtractionStatus.Succeeded, e.Status);
        Assert.Equal(ExtractionSource.Llm, e.Source);
        Assert.Null(e.Error);
        Assert.Null(e.EditedBy);
        Assert.Equal("claude-sonnet-4-6", e.Model);
        Assert.Equal(5000, e.InputTokens);
        Assert.Equal(400, e.OutputTokens);
    }

    [Fact]
    public void Succeeded_LlmRows_AreAutoConfirmed_ForP3ReaderCompat()
    {
        // Spec §"Document store schema" — validators read latest row WHERE
        // confirmed_at IS NOT NULL. P3 has no confirmation UX, so LLM rows must
        // land confirmed at write time or the aggregator sees zero rows. P5 will
        // add the explicit confirmation step for manual-edit rows (which land
        // with ConfirmedAt = null). This test pins the rule so it doesn't drift
        // when P5 starts changing the surrounding code.
        var fixedNow = new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);
        var e = DocumentExtraction.CreateLlmSucceeded(
            Guid.NewGuid(), 1, "license.v1", Fields, Locations, Confidence,
            "claude-sonnet-4-6", new string('a', 64), 1, 1, now: fixedNow);

        Assert.NotNull(e.ConfirmedAt);
        Assert.Equal(fixedNow, e.ExtractedAt);
        Assert.Equal(fixedNow, e.ConfirmedAt);
    }

    [Fact]
    public void Failed_EnforcesEmptyJsonShape()
    {
        var e = Failed();

        Assert.Equal(ExtractionStatus.Failed, e.Status);
        Assert.Equal("{}", e.FieldsJson);
        Assert.Equal("{}", e.FieldLocationsJson);
        Assert.Equal("{}", e.ConfidenceJson);
        Assert.Equal("structured-output parse failed", e.Error);

        // Failed rows are not auto-confirmed — the aggregator filters on status
        // anyway, but keeping confirmed_at null preserves the "ready for
        // downstream" semantic.
        Assert.Null(e.ConfirmedAt);
    }

    [Fact]
    public void CreateLlmSucceeded_RejectsZeroOrNegativeExtractionId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), 0, "license.v1", Fields, Locations, Confidence, "m", "h", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), -1, "license.v1", Fields, Locations, Confidence, "m", "h", 1, 1));
    }

    [Fact]
    public void CreateLlmSucceeded_RejectsEmptyDocumentId()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.Empty, 1, "license.v1", Fields, Locations, Confidence, "m", "h", 1, 1));
        Assert.Equal("documentId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateLlmSucceeded_RejectsBlankSchemaVersion(string blank)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), 1, blank, Fields, Locations, Confidence, "m", "h", 1, 1));
        Assert.Equal("schemaVersion", ex.ParamName);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{unterminated")]
    public void CreateLlmSucceeded_RejectsInvalidFieldsJson(string badJson)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), 1, "license.v1", badJson, Locations, Confidence, "m", "h", 1, 1));
        Assert.Equal("fieldsJson", ex.ParamName);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"oops\"")]
    [InlineData("[1, 2, 3]")]
    public void CreateLlmSucceeded_RejectsNonObjectJson(string nonObjectJson)
    {
        // Postgres jsonb accepts scalars and arrays too; the factory rejects them
        // so the aggregator can deserialize to a dictionary without defensive
        // type checks at every reader. Spec semantically requires objects:
        // `{ field: ... }`, `{ field: 0.97 }`, `{ field: { page, bbox } }`.
        var ex = Assert.Throws<ArgumentException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), 1, "license.v1", nonObjectJson, Locations, Confidence, "m", "h", 1, 1));
        Assert.Equal("fieldsJson", ex.ParamName);
    }

    [Fact]
    public void CreateLlmSucceeded_RejectsBlankModelOrPromptHash()
    {
        Assert.Throws<ArgumentException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), 1, "license.v1", Fields, Locations, Confidence, "", "h", 1, 1));
        Assert.Throws<ArgumentException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), 1, "license.v1", Fields, Locations, Confidence, "m", "", 1, 1));
    }

    [Fact]
    public void CreateLlmSucceeded_RejectsNegativeTokenCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), 1, "license.v1", Fields, Locations, Confidence, "m", "h", -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentExtraction.CreateLlmSucceeded(
                Guid.NewGuid(), 1, "license.v1", Fields, Locations, Confidence, "m", "h", 1, -1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateLlmFailed_RejectsBlankError(string blank)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DocumentExtraction.CreateLlmFailed(Guid.NewGuid(), 1, "license.v1", blank, "m", "h"));
        Assert.Equal("error", ex.ParamName);
    }

    [Fact]
    public void CreateLlmFailed_AllowsNullTokenCounts()
    {
        // The model may have errored before returning a usage block. Token counts
        // are optional on Failed; the row still carries the model and prompt
        // hash so the audit trail is intact.
        var e = DocumentExtraction.CreateLlmFailed(
            Guid.NewGuid(), 1, "license.v1", "boom", "claude-sonnet-4-6", new string('a', 64));
        Assert.Null(e.InputTokens);
        Assert.Null(e.OutputTokens);
    }
}
