using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Prompts;
using PacketReady.Infrastructure.Extraction.SonnetExtractors;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Extraction;

/// <summary>
/// Live Anthropic call against the real <c>packet-001-clean-anderson/license.pdf</c>
/// fixture. <b>Skipped by default</b> — runs only when both
/// <c>PACKETREADY_LIVE_LLM=1</c> and <c>ANTHROPIC_API_KEY</c> are present in the
/// environment. CI is configured without either, so this never bills Anthropic
/// outside an opt-in local run.
///
/// <para>This is the canary for slice 4's "does Sonnet 4.6 actually return usable
/// structured output against a real license PDF" question. If license accuracy
/// is under 80% on the four non-scanned packets, the spec says: fix the prompt
/// before building Dea / BoardCert / Malpractice extractors.</para>
/// </summary>
public class LicenseExtractorLiveTests
{
    [LiveLlmFact]
    public async Task ExtractAsync_AgainstPacket001License_ReturnsHenryAnderson()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;

        var datasetRoot = TestPaths.LocateDatasetRoot();
        var pdfPath = Path.Combine(datasetRoot, "packet-001-clean-anderson", "license.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        var anthropicClient = new AnthropicClient(apiKey);
        IChatClient chat = anthropicClient.Messages;

        var prompts = new PromptLoader();
        var hasher = new PromptHasher(prompts);

        var extractor = new LicenseExtractor(
            chat,
            prompts,
            hasher,
            NullLogger<LicenseExtractor>.Instance);

        var result = await extractor.ExtractAsync(pdfBytes, CancellationToken.None);

        Assert.Equal("claude-sonnet-4-6", result.Model);
        Assert.Equal(64, result.PromptHash.Length);
        Assert.True(result.InputTokens > 0, "Anthropic should report input tokens.");
        Assert.True(result.OutputTokens > 0, "Anthropic should report output tokens.");

        // Spot-check the extracted values. Sonnet's normalization may vary slightly
        // (e.g. "MD" vs "M.D."), so substring rather than equality.
        using var fields = System.Text.Json.JsonDocument.Parse(result.FieldsJson);
        var fullName = fields.RootElement.GetProperty("fullName").GetString();
        var licenseNumber = fields.RootElement.GetProperty("licenseNumber").GetString();
        var state = fields.RootElement.GetProperty("state").GetString();

        Assert.NotNull(fullName);
        Assert.Contains("Anderson", fullName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(licenseNumber);
        Assert.Contains("99001", licenseNumber);
        Assert.Equal("NY", state);
    }
}
