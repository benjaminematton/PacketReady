using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;
using PacketReady.Infrastructure.Extraction.Classifier;
using PacketReady.Tests.Infrastructure.Extraction;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Classifier;

/// <summary>
/// Live Haiku 4.5 classification against every PDF in
/// <c>packet-001-clean-anderson/</c>. <see cref="LiveLlmFactAttribute"/>-gated.
///
/// <para>This is the slice-7A canary: does Haiku correctly classify each of
/// the four doc types with ≥ 0.85 confidence? Spec says "every PDF classifies
/// correctly with confidence ≥ 0.85 or P3 doesn't ship" — that's the
/// confidence floor pinned here, not perfect accuracy across the full
/// distribution.</para>
/// </summary>
public class HaikuDocumentClassifierLiveTests
{
    [LiveLlmFact]
    public async Task ClassifyAsync_LicensePdf_ReturnsLicenseHighConfidence()
        => await AssertClassifies("license.pdf", DocType.License);

    [LiveLlmFact]
    public async Task ClassifyAsync_DeaPdf_ReturnsDeaHighConfidence()
        => await AssertClassifies("dea.pdf", DocType.Dea);

    [LiveLlmFact]
    public async Task ClassifyAsync_BoardCertPdf_ReturnsBoardCertHighConfidence()
        => await AssertClassifies("board-cert.pdf", DocType.BoardCert);

    [LiveLlmFact]
    public async Task ClassifyAsync_MalpracticePdf_ReturnsMalpracticeHighConfidence()
        => await AssertClassifies("malpractice.pdf", DocType.Malpractice);

    private static async Task AssertClassifies(string filename, DocType expected)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;

        var pdfPath = Path.Combine(
            TestPaths.LocateDatasetRoot(), "packet-001-clean-anderson", filename);
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        var chat = (IChatClient)new AnthropicClient(apiKey).Messages;
        var prompts = new PromptLoader();
        var hasher = new PromptHasher(prompts);

        var classifier = new HaikuDocumentClassifier(
            chat,
            prompts,
            hasher,
            NullLogger<HaikuDocumentClassifier>.Instance);

        var result = await classifier.ClassifyAsync(pdfBytes, CancellationToken.None);

        Assert.Equal(expected, result.DocType);
        Assert.True(
            result.Confidence >= 0.85,
            $"Expected confidence ≥ 0.85 for {filename}, got {result.Confidence:F2}.");
        Assert.Equal("claude-haiku-4-5", result.Model);
        Assert.Equal(64, result.PromptHash.Length);
        Assert.True(result.InputTokens > 0);
        Assert.True(result.OutputTokens > 0);
    }
}
