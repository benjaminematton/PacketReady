using PacketReady.Domain.Documents;
using PacketReady.Infrastructure.Extraction.Classifier;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Classifier;

/// <summary>
/// Pure-code coverage for <see cref="HaikuDocumentClassifier.ParseResponse"/>.
/// The live Anthropic call is exercised by <c>HaikuDocumentClassifierLiveTests</c>;
/// this file pins the wire-shape → enum mapping + validation that runs on
/// whatever Haiku returns.
/// </summary>
public class HaikuDocumentClassifierTests
{
    [Theory]
    [InlineData("license", DocType.License)]
    [InlineData("dea", DocType.Dea)]
    [InlineData("boardCert", DocType.BoardCert)]
    [InlineData("malpractice", DocType.Malpractice)]
    [InlineData("cv", DocType.Cv)]
    [InlineData("other", DocType.Other)]
    public void Parse_MapsEveryWireLabelToEnum(string wire, DocType expected)
    {
        var raw = $$"""{ "docType": "{{wire}}", "confidence": 0.95, "rationale": "x" }""";

        var (docType, _, _) = HaikuDocumentClassifier.ParseResponse(raw);

        Assert.Equal(expected, docType);
    }

    [Fact]
    public void Parse_PassesThroughRationale()
    {
        var raw = """{"docType":"license","confidence":0.91,"rationale":"Title and license-number field present"}""";

        var (_, _, rationale) = HaikuDocumentClassifier.ParseResponse(raw);

        Assert.Equal("Title and license-number field present", rationale);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(-0.1, 0.0)]   // clamped low
    [InlineData(1.5, 1.0)]    // clamped high
    public void Parse_ClampsConfidenceToUnitInterval(double input, double expected)
    {
        // Anthropic's schema can't enforce numeric ranges; the parser clamps so
        // downstream banding logic ([≥0.85] / [0.50–0.85] / [<0.50]) always has
        // a usable value. A model returning 1.5 is more likely a prompt-following
        // bug than a meaningful overclaim — clamp, don't reject.
        var raw = $$"""{ "docType": "license", "confidence": {{input}}, "rationale": "x" }""";

        var (_, confidence, _) = HaikuDocumentClassifier.ParseResponse(raw);

        Assert.Equal(expected, confidence);
    }

    [Fact]
    public void Parse_ThrowsOnUnmappedDocType()
    {
        var raw = """{"docType":"passport","confidence":0.5,"rationale":"x"}""";

        var ex = Assert.Throws<ClassifierResponseException>(
            () => HaikuDocumentClassifier.ParseResponse(raw));
        Assert.Contains("passport", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{unterminated")]
    [InlineData("[1, 2, 3]")]
    public void Parse_ThrowsOnNonObjectOrInvalidJson(string raw)
    {
        Assert.Throws<ClassifierResponseException>(
            () => HaikuDocumentClassifier.ParseResponse(raw));
    }

    [Theory]
    [InlineData("""{"confidence":0.9,"rationale":"x"}""", "docType")]
    [InlineData("""{"docType":"license","rationale":"x"}""", "confidence")]
    [InlineData("""{"docType":"license","confidence":0.9}""", "rationale")]
    public void Parse_ThrowsWhenRequiredFieldMissing(string raw, string missing)
    {
        var ex = Assert.Throws<ClassifierResponseException>(
            () => HaikuDocumentClassifier.ParseResponse(raw));
        Assert.Contains($"'{missing}'", ex.Message);
    }

    [Fact]
    public void Parse_ThrowsWhenDocTypeIsNotString()
    {
        var raw = """{"docType":123,"confidence":0.9,"rationale":"x"}""";
        var ex = Assert.Throws<ClassifierResponseException>(
            () => HaikuDocumentClassifier.ParseResponse(raw));
        Assert.Contains("docType", ex.Message);
    }

    [Fact]
    public void Parse_ThrowsWhenConfidenceIsNotNumber()
    {
        // The prompt insists confidence be the literal number 0.00 (never null),
        // for exactly this reason — a null here would silently bypass the
        // banding logic. Reject loud.
        var raw = """{"docType":"license","confidence":null,"rationale":"x"}""";
        var ex = Assert.Throws<ClassifierResponseException>(
            () => HaikuDocumentClassifier.ParseResponse(raw));
        Assert.Contains("confidence", ex.Message);
    }
}
