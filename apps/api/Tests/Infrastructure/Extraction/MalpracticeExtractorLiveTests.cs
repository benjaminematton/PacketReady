using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Prompts;
using PacketReady.Infrastructure.Extraction.SonnetExtractors;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Extraction;

/// <summary>
/// Live Anthropic call against <c>packet-001-clean-anderson/malpractice.pdf</c>.
/// <see cref="LiveLlmFactAttribute"/>-gated; skipped unless
/// <c>PACKETREADY_LIVE_LLM=1</c> + <c>ANTHROPIC_API_KEY</c> are set.
/// </summary>
public class MalpracticeExtractorLiveTests
{
    [LiveLlmFact]
    public async Task ExtractAsync_AgainstPacket001Malpractice_ReturnsMedProtectPolicy()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;

        var pdfPath = Path.Combine(TestPaths.LocateDatasetRoot(), "packet-001-clean-anderson", "malpractice.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        var anthropicClient = new AnthropicClient(apiKey);
        IChatClient chat = anthropicClient.Messages;

        var prompts = new PromptLoader();
        var hasher = new PromptHasher(prompts);

        var extractor = new MalpracticeExtractor(
            chat,
            prompts,
            hasher,
            NullLogger<MalpracticeExtractor>.Instance);

        var result = await extractor.ExtractAsync(pdfBytes, CancellationToken.None);

        Assert.Equal("claude-sonnet-4-6", result.Model);
        Assert.Equal(64, result.PromptHash.Length);
        Assert.True(result.InputTokens > 0);
        Assert.True(result.OutputTokens > 0);

        using var fields = System.Text.Json.JsonDocument.Parse(result.FieldsJson);
        var carrier = fields.RootElement.GetProperty("carrier").GetString();
        var policyNumber = fields.RootElement.GetProperty("policyNumber").GetString();

        // Carrier verbatim from the certificate; substring tolerates "MedProtect"
        // vs "MedProtect Mutual" depending on which form the document carries.
        Assert.NotNull(carrier);
        Assert.Contains("MedProtect", carrier, StringComparison.OrdinalIgnoreCase);

        // Policy number includes dashes per the prompt's verbatim rule.
        Assert.NotNull(policyNumber);
        Assert.Contains("MPM-NY", policyNumber);
        Assert.Contains("00099001", policyNumber);
    }
}
