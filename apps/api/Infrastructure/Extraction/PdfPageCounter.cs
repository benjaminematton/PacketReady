using UglyToad.PdfPig;

namespace PacketReady.Infrastructure.Extraction;

/// <summary>
/// Reads two facts off a PDF that the extraction pipeline needs before invoking
/// Sonnet: page count (for <c>documents.page_count</c>) and presence of a text
/// layer (the "scanned PDF" signal — Sonnet self-reports bboxes confidently on
/// native PDFs, degrades to page-level on rasterized inputs).
///
/// <para>Pure-managed via UglyToad.PdfPig; no native dependencies. Throws
/// <see cref="InvalidPdfException"/> for input bytes the library cannot parse —
/// the upload handler turns that into a 400, never persisting an unreadable PDF.</para>
/// </summary>
public sealed class PdfPageCounter
{
    public sealed record PdfFacts(int PageCount, bool HasTextLayer);

    public PdfFacts Read(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length == 0)
            throw new InvalidPdfException("PDF bytes are empty.");

        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(pdfBytes);
        }
        catch (Exception ex)
        {
            // PdfPig wraps low-level errors as its own exception types; we
            // collapse all of them to InvalidPdfException so callers have one
            // signal to handle. Library-specific exceptions don't leak past
            // this boundary.
            throw new InvalidPdfException(
                $"PDF could not be parsed: {ex.Message}", ex);
        }

        using (doc)
        {
            int pageCount = doc.NumberOfPages;

            // "Has text layer" = ≥ TextLayerCharThreshold extracted chars across
            // the first PagesToSample pages. The threshold defends against
            // single-watermark false positives ("DRAFT", Bates stamps, page
            // numbers on a scanned PDF). Scanning only the first few pages
            // bounds the cost on large native PDFs; if those pages have a
            // text layer the rest almost certainly do too, and if they don't
            // we treat the whole doc as scanned regardless.
            const int TextLayerCharThreshold = 50;
            const int PagesToSample = 3;

            int charsSeen = 0;
            int upper = Math.Min(pageCount, PagesToSample);
            for (int i = 1; i <= upper && charsSeen < TextLayerCharThreshold; i++)
            {
                var pageText = doc.GetPage(i).Text;
                if (!string.IsNullOrWhiteSpace(pageText))
                    charsSeen += pageText.Length;
            }

            return new PdfFacts(pageCount, charsSeen >= TextLayerCharThreshold);
        }
    }
}

public sealed class InvalidPdfException : Exception
{
    public InvalidPdfException(string message) : base(message) { }
    public InvalidPdfException(string message, Exception inner) : base(message, inner) { }
}
