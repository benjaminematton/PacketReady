using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Prompts;
using PacketReady.Infrastructure.Extraction.SonnetExtractors;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Extraction;

/// <summary>
/// Live Anthropic call against <c>packet-001-clean-anderson/dea.pdf</c>.
/// <see cref="LiveLlmFactAttribute"/>-gated; skipped unless
/// <c>PACKETREADY_LIVE_LLM=1</c> + <c>ANTHROPIC_API_KEY</c> are set.
///
/// <para>Pins the schedules-array path — the only array-valued field across
/// the four P3 extractors. If this passes, the response splitter's "pass the
/// value through unchanged" claim holds for arrays.</para>
/// </summary>
public class DeaExtractorLiveTests
{
    [LiveLlmFact]
    public async Task ExtractAsync_AgainstPacket001Dea_ReturnsHenryAndersonSchedulesII_V()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;

        var pdfPath = Path.Combine(TestPaths.LocateDatasetRoot(), "packet-001-clean-anderson", "dea.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        var anthropicClient = new AnthropicClient(apiKey);
        IChatClient chat = anthropicClient.Messages;

        var prompts = new PromptLoader();
        var hasher = new PromptHasher(prompts);

        var extractor = new DeaExtractor(
            chat,
            prompts,
            hasher,
            NullLogger<DeaExtractor>.Instance);

        var result = await extractor.ExtractAsync(pdfBytes, CancellationToken.None);

        Assert.Equal("claude-sonnet-4-6", result.Model);
        Assert.Equal(64, result.PromptHash.Length);
        Assert.True(result.InputTokens > 0);
        Assert.True(result.OutputTokens > 0);

        using var fields = System.Text.Json.JsonDocument.Parse(result.FieldsJson);
        var fullName = fields.RootElement.GetProperty("fullName").GetString();
        var deaNumber = fields.RootElement.GetProperty("deaNumber").GetString();
        var schedules = fields.RootElement.GetProperty("schedules");

        Assert.NotNull(fullName);
        Assert.Contains("Anderson", fullName, StringComparison.OrdinalIgnoreCase);

        // DEA number format: two letters + seven digits, no whitespace.
        Assert.NotNull(deaNumber);
        Assert.Equal(9, deaNumber.Length);
        Assert.Equal("BA1234567", deaNumber);

        // Schedules: golden says II/III/IV/V (full coverage). Order may vary; the
        // prompt asks for "as printed" but we assert membership, not order.
        Assert.Equal(System.Text.Json.JsonValueKind.Array, schedules.ValueKind);
        var scheduleSet = new HashSet<string>(
            schedules.EnumerateArray().Select(e => e.GetString() ?? ""));
        Assert.Contains("II", scheduleSet);
        Assert.Contains("III", scheduleSet);
        Assert.Contains("IV", scheduleSet);
        Assert.Contains("V", scheduleSet);
    }
}
