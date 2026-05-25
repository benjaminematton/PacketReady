using System.Text.Json;
using PacketReady.Infrastructure.Extraction.SonnetExtractors;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Extraction;

/// <summary>
/// Pure-code coverage for the response-splitting half of <c>SonnetExtractorBase</c>.
/// The LLM call itself is exercised by the env-guarded live test against
/// <c>packet-001-clean-anderson/license.pdf</c>; this file pins the deterministic
/// transformation that runs on whatever Sonnet returns.
/// </summary>
public class SonnetExtractorBaseTests
{
    [Fact]
    public void Split_SeparatesValuesLocationsAndConfidence()
    {
        var raw = """
        {
          "fields": {
            "fullName":      { "value": "Henry Anderson, MD", "page": 1, "bbox": [120, 240, 380, 22] },
            "licenseNumber": { "value": "MD-NY-99001",        "page": 1, "bbox": [120, 280, 200, 22] }
          },
          "confidence": {
            "fullName": 0.97,
            "licenseNumber": 0.98
          }
        }
        """;

        var (fields, locs, conf) = SonnetExtractorBase.SplitLlmResponse(raw);

        // fields → value-only map
        using (var doc = JsonDocument.Parse(fields))
        {
            Assert.Equal("Henry Anderson, MD", doc.RootElement.GetProperty("fullName").GetString());
            Assert.Equal("MD-NY-99001", doc.RootElement.GetProperty("licenseNumber").GetString());
        }

        // field_locations → page + bbox per field
        using (var doc = JsonDocument.Parse(locs))
        {
            var fn = doc.RootElement.GetProperty("fullName");
            Assert.Equal(1, fn.GetProperty("page").GetInt32());
            Assert.Equal(4, fn.GetProperty("bbox").GetArrayLength());
            Assert.Equal(120, fn.GetProperty("bbox")[0].GetDouble());
        }

        // confidence passed through verbatim
        using (var doc = JsonDocument.Parse(conf))
        {
            Assert.Equal(0.97, doc.RootElement.GetProperty("fullName").GetDouble());
            Assert.Equal(0.98, doc.RootElement.GetProperty("licenseNumber").GetDouble());
        }
    }

    [Fact]
    public void Split_PreservesArrayValues_Unchanged()
    {
        // DEA's `schedules` is the only array-valued field across the four P3
        // extractors. The splitter must pass arrays into FieldsJson byte-for-byte
        // so downstream consumers (aggregator, profile builder) see the array
        // shape they expect — not a stringified or flattened version.
        var raw = """
        {
          "fields": {
            "schedules": { "value": ["II", "IV"], "page": 1, "bbox": [120, 400, 200, 22] }
          },
          "confidence": { "schedules": 0.96 }
        }
        """;

        var (fields, _, _) = SonnetExtractorBase.SplitLlmResponse(raw);

        using var doc = JsonDocument.Parse(fields);
        var schedules = doc.RootElement.GetProperty("schedules");
        Assert.Equal(JsonValueKind.Array, schedules.ValueKind);
        Assert.Equal(2, schedules.GetArrayLength());
        Assert.Equal("II", schedules[0].GetString());
        Assert.Equal("IV", schedules[1].GetString());
    }

    [Fact]
    public void Split_PreservesNullValues_InsteadOfDroppingThem()
    {
        // A field the LLM couldn't read returns value=null per the prompt spec.
        // The value-only fields JSONB must carry the null so DocumentExtraction
        // can persist it and the aggregator can see "field absent" explicitly.
        var raw = """
        {
          "fields": {
            "fullName": { "value": null, "page": 1, "bbox": [0, 0, 0, 0] }
          },
          "confidence": { "fullName": 0.00 }
        }
        """;

        var (fields, _, _) = SonnetExtractorBase.SplitLlmResponse(raw);

        using var doc = JsonDocument.Parse(fields);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("fullName").ValueKind);
    }

    [Fact]
    public void Split_ThrowsOnNonObjectRoot()
    {
        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse("[1, 2, 3]"));
        Assert.Contains("not a JSON object", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Split_ThrowsWhenFieldsMissing()
    {
        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse("""{"confidence":{}}"""));
        Assert.Contains("'fields'", ex.Message);
    }

    [Fact]
    public void Split_ThrowsWhenConfidenceMissing()
    {
        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse("""{"fields":{}}"""));
        Assert.Contains("'confidence'", ex.Message);
    }

    [Fact]
    public void Split_ThrowsWhenFieldEnvelopeMissingValue()
    {
        // Schema-enforced output should always include {value,page,bbox} per field.
        // Missing any of those three is an LLM contract violation, not recoverable
        // input — fail loud rather than silently writing a malformed row.
        var raw = """
        {
          "fields":     { "fullName": { "page": 1, "bbox": [0, 0, 0, 0] } },
          "confidence": { "fullName": 0.9 }
        }
        """;

        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse(raw));
        Assert.Contains("missing 'value'", ex.Message);
    }

    [Fact]
    public void Split_ThrowsOnMalformedJson()
    {
        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse("{unterminated"));
        Assert.Contains("not valid JSON", ex.Message);
    }
}
