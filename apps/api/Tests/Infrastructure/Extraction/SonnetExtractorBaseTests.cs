using System.Text.Json;
using Microsoft.Extensions.AI;
using PacketReady.Infrastructure.Extraction;
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

    [Fact]
    public void Split_AttachesTruncationHintWhenOutputNearCap()
    {
        // Unparseable response + outputTokens near the 2048 cap → the error
        // message should call out likely truncation so operators don't chase
        // a prompt bug. 1900 > 0.9 * 2048 = 1843.
        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse("{unterminated", outputTokens: 1900));
        Assert.Contains("likely truncated", ex.Message);
        Assert.Contains("1900", ex.Message);
    }

    [Fact]
    public void Split_ThrowsWhenPageBelowOne()
    {
        var raw = """
        {
          "fields":     { "fullName": { "value": "x", "page": 0, "bbox": [0, 0, 1, 1] } },
          "confidence": { "fullName": 0.9 }
        }
        """;

        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse(raw));
        Assert.Contains("'page'", ex.Message);
        Assert.Contains("≥ 1", ex.Message);
    }

    [Fact]
    public void Split_ThrowsWhenBboxIsWrongLength()
    {
        var raw = """
        {
          "fields":     { "fullName": { "value": "x", "page": 1, "bbox": [0, 0, 1] } },
          "confidence": { "fullName": 0.9 }
        }
        """;

        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse(raw));
        Assert.Contains("'bbox'", ex.Message);
        Assert.Contains("4 numbers", ex.Message);
    }

    [Fact]
    public void Split_ThrowsWhenBboxCoordinateIsNonFinite()
    {
        // System.Text.Json rejects bare NaN, but a string masquerading as a
        // number gets caught here too — the validator must reject anything
        // that isn't a real, finite number.
        var raw = """
        {
          "fields":     { "fullName": { "value": "x", "page": 1, "bbox": [0, 0, 1, "oops"] } },
          "confidence": { "fullName": 0.9 }
        }
        """;

        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse(raw));
        Assert.Contains("non-finite 'bbox'", ex.Message);
    }

    [Fact]
    public void Split_ThrowsWhenConfidenceOutOfRange()
    {
        var raw = """
        {
          "fields":     { "fullName": { "value": "x", "page": 1, "bbox": [0, 0, 1, 1] } },
          "confidence": { "fullName": 1.5 }
        }
        """;

        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse(raw));
        Assert.Contains("'confidence'", ex.Message);
        Assert.Contains("[0, 1]", ex.Message);
    }

    [Fact]
    public void Split_ThrowsWhenConfidenceIsNullInsteadOfZero()
    {
        // The prompts pin "use 0.00, not null" for absent fields — if Sonnet
        // ever drifts and emits null, the validator must catch it rather than
        // let a null land in the confidence JSONB.
        var raw = """
        {
          "fields":     { "fullName": { "value": null, "page": 1, "bbox": [0, 0, 0, 0] } },
          "confidence": { "fullName": null }
        }
        """;

        var ex = Assert.Throws<ExtractorResponseException>(
            () => SonnetExtractorBase.SplitLlmResponse(raw));
        Assert.Contains("'confidence'", ex.Message);
    }

    [Fact]
    public void ExtractStructuredJson_PrefersFunctionCallOverPlainText()
    {
        // M.E.AI's Anthropic adapter for ChatResponseFormat.ForJsonSchema is
        // implemented as a forced tool call; if a future build also surfaces
        // wrapper text, the function-call arguments must win — otherwise we
        // feed the wrapper into JsonDocument.Parse and get an opaque error.
        var args = new Dictionary<string, object?>
        {
            ["fields"] = new Dictionary<string, object?>(),
            ["confidence"] = new Dictionary<string, object?>(),
        };
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent("Here's the data:"),
            new FunctionCallContent("call_1", "license_extraction", args),
        });
        var response = new ChatResponse(message);

        var raw = ChatResponseParser.ExtractStructuredJson(response);

        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("fields").ValueKind);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("confidence").ValueKind);
    }

    [Fact]
    public void ExtractStructuredJson_FallsBackToTextWhenNoFunctionCall()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"fields":{},"confidence":{}}"""));

        var raw = ChatResponseParser.ExtractStructuredJson(response);

        Assert.Equal("""{"fields":{},"confidence":{}}""", raw);
    }
}
