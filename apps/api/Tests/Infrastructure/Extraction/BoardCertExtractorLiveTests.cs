using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Prompts;
using PacketReady.Infrastructure.Extraction.SonnetExtractors;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Extraction;

/// <summary>
/// Live Anthropic call against <c>packet-001-clean-anderson/board-cert.pdf</c>.
/// <see cref="LiveLlmFactAttribute"/>-gated; skipped unless
/// <c>PACKETREADY_LIVE_LLM=1</c> + <c>ANTHROPIC_API_KEY</c> are set.
/// </summary>
public class BoardCertExtractorLiveTests
{
    [LiveLlmFact]
    public async Task ExtractAsync_AgainstPacket001BoardCert_ReturnsABIMInternalMedicine()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;

        var pdfPath = Path.Combine(TestPaths.LocateDatasetRoot(), "packet-001-clean-anderson", "board-cert.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        var anthropicClient = new AnthropicClient(apiKey);
        IChatClient chat = anthropicClient.Messages;

        var prompts = new PromptLoader();
        var hasher = new PromptHasher(prompts);

        var extractor = new BoardCertExtractor(
            chat,
            prompts,
            hasher,
            NullLogger<BoardCertExtractor>.Instance);

        var result = await extractor.ExtractAsync(pdfBytes, CancellationToken.None);

        Assert.Equal("claude-sonnet-4-6", result.Model);
        Assert.Equal(64, result.PromptHash.Length);
        Assert.True(result.InputTokens > 0);
        Assert.True(result.OutputTokens > 0);

        using var fields = System.Text.Json.JsonDocument.Parse(result.FieldsJson);
        var board = fields.RootElement.GetProperty("board").GetString();
        var specialty = fields.RootElement.GetProperty("specialty").GetString();

        // Prompt normalizes the board long form to its acronym. Golden truth: ABIM.
        Assert.Equal("ABIM", board);

        // Specialty is "Internal Medicine" in golden; substring guards against
        // minor LLM casing drift ("internal medicine").
        Assert.NotNull(specialty);
        Assert.Contains("Internal Medicine", specialty, StringComparison.OrdinalIgnoreCase);
    }
}
