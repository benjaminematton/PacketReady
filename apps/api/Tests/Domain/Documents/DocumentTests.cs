using PacketReady.Domain.Documents;
using Xunit;

namespace PacketReady.Tests.Domain.Documents;

public class DocumentTests
{
    private static Document ValidDocument(
        DocType? docType = DocType.License,
        double? confidence = 0.95,
        int pageCount = 3) =>
        Document.Create(
            providerId: Guid.NewGuid(),
            docType: docType,
            docTypeConfidence: confidence,
            classifierModel: "claude-haiku-4-5",
            classifierPromptHash: new string('a', 64),
            storageUri: "file:///tmp/x.pdf",
            originalName: "license.pdf",
            mimeType: "application/pdf",
            pageCount: pageCount,
            uploadedBy: Uploader.Provider);

    [Fact]
    public void Create_PopulatesIdAndTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var doc = ValidDocument();
        var after = DateTimeOffset.UtcNow;

        Assert.NotEqual(Guid.Empty, doc.Id);
        Assert.InRange(doc.UploadedAt, before, after);
    }

    [Fact]
    public void Create_RejectsEmptyProviderId()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Document.Create(
                providerId: Guid.Empty,
                docType: DocType.License,
                docTypeConfidence: 0.9,
                classifierModel: "m",
                classifierPromptHash: "h",
                storageUri: "u",
                originalName: "n",
                mimeType: "t",
                pageCount: 1,
                uploadedBy: Uploader.Provider));
        Assert.Equal("providerId", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RejectsNonPositivePageCount(int pageCount)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ValidDocument(pageCount: pageCount));
        Assert.Equal("pageCount", ex.ParamName);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Create_RejectsOutOfRangeConfidence(double confidence)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ValidDocument(confidence: confidence));
        Assert.Equal("docTypeConfidence", ex.ParamName);
    }

    [Fact]
    public void Create_RejectsMixedNullDocTypeAndConfidence()
    {
        // doc_type set, confidence null
        var ex1 = Assert.Throws<ArgumentException>(() => ValidDocument(docType: DocType.License, confidence: null));
        Assert.Equal("docTypeConfidence", ex1.ParamName);

        // confidence set, doc_type null
        var ex2 = Assert.Throws<ArgumentException>(() => ValidDocument(docType: null, confidence: 0.5));
        Assert.Equal("docType", ex2.ParamName);
    }

    [Fact]
    public void Create_AllowsBothDocTypeAndConfidenceNull()
    {
        // Classifier-failure persistence path: row lands without a classification.
        var doc = ValidDocument(docType: null, confidence: null);
        Assert.Null(doc.DocType);
        Assert.Null(doc.DocTypeConfidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsBlankRequiredStrings(string blank)
    {
        Assert.Throws<ArgumentException>(() =>
            Document.Create(Guid.NewGuid(), DocType.License, 0.9, blank, "h", "u", "n", "t", 1, Uploader.Provider));
        Assert.Throws<ArgumentException>(() =>
            Document.Create(Guid.NewGuid(), DocType.License, 0.9, "m", blank, "u", "n", "t", 1, Uploader.Provider));
        Assert.Throws<ArgumentException>(() =>
            Document.Create(Guid.NewGuid(), DocType.License, 0.9, "m", "h", blank, "n", "t", 1, Uploader.Provider));
        Assert.Throws<ArgumentException>(() =>
            Document.Create(Guid.NewGuid(), DocType.License, 0.9, "m", "h", "u", blank, "t", 1, Uploader.Provider));
        Assert.Throws<ArgumentException>(() =>
            Document.Create(Guid.NewGuid(), DocType.License, 0.9, "m", "h", "u", "n", blank, 1, Uploader.Provider));
    }
}
