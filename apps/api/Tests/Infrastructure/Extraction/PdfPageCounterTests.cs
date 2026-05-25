using PacketReady.Infrastructure.Extraction;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Extraction;

public class PdfPageCounterTests
{
    [Fact]
    public void Read_CountsPagesAndDetectsTextLayer_OnNativePdf()
    {
        var datasetRoot = TestPaths.LocateDatasetRoot();
        var bytes = File.ReadAllBytes(Path.Combine(datasetRoot, "packet-001-clean-anderson", "license.pdf"));

        var counter = new PdfPageCounter();
        var facts = counter.Read(bytes);

        Assert.True(facts.PageCount >= 1, $"Expected ≥ 1 page, got {facts.PageCount}.");
        Assert.True(facts.HasTextLayer, "packet-001 license is a native PDF — should have a text layer.");
    }

    [Fact]
    public void Read_RejectsEmptyBytes()
    {
        Assert.Throws<InvalidPdfException>(() => new PdfPageCounter().Read(Array.Empty<byte>()));
    }

    [Fact]
    public void Read_WrapsParseErrors_InInvalidPdfException()
    {
        // Bytes that aren't a PDF — PdfPig throws something library-specific; the
        // counter must collapse to InvalidPdfException so the upload handler has
        // a single signal to return 400.
        var garbage = System.Text.Encoding.ASCII.GetBytes("not a pdf, just text");
        Assert.Throws<InvalidPdfException>(() => new PdfPageCounter().Read(garbage));
    }
}
