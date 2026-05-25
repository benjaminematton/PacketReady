using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PacketReady.Application.Documents;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Application.Extraction.Persist;
using PacketReady.Application.Extraction.Reextract;
using PacketReady.Domain.Documents;
using PacketReady.Infrastructure.Persistence;
using PacketReady.Tests.Infrastructure;
using Xunit;

namespace PacketReady.Tests.Application.Extraction.Reextract;

/// <summary>
/// Branch coverage for <see cref="ReextractDocumentCommandHandler"/>. Same
/// pattern as the upload tests: EF InMemory + Moqs of blob + persister + a
/// real keyed-service provider with mock extractors.
/// </summary>
public class ReextractDocumentCommandHandlerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_HappyPath_LoadsBlobAndPersistsNewExtraction()
    {
        var fx = await new Fixture().WithDocument(DocType.License, conf: 0.95);

        var result = await fx.Handler.Handle(
            new ReextractDocumentCommand(fx.DocumentId), CancellationToken.None);

        Assert.Equal(fx.DocumentId, result.DocumentId);
        Assert.Equal(DocType.License, result.DocType);
        Assert.Equal(0.95, result.DocTypeConfidence);
        Assert.Equal(7, result.ExtractionId);
        Assert.False(result.WasCacheHit);

        fx.Blobs.Verify(b => b.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        fx.Persister.Verify(p => p.PersistAsync(
            It.Is<Document>(d => d.Id == fx.DocumentId),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IDocTypeExtractor>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_CacheHit_PropagatesWasCacheHitTrue()
    {
        // Reextract against unchanged (model, prompt_hash) — the persister
        // returns the existing extractionId with WasCacheHit=true; the
        // handler propagates that to the endpoint's 200/201 split.
        var fx = await new Fixture().WithDocument(DocType.License, conf: 0.95);
        fx.PersisterReturns(new ExtractionPersistResult(
            ExtractionId: 3, WasCacheHit: true, Status: ExtractionStatus.Succeeded));

        var result = await fx.Handler.Handle(
            new ReextractDocumentCommand(fx.DocumentId), CancellationToken.None);

        Assert.Equal(3, result.ExtractionId);
        Assert.True(result.WasCacheHit);
    }

    [Fact]
    public async Task Handle_ThrowsDocumentNotFound_WhenDocumentMissing()
    {
        var fx = new Fixture();   // no document seeded

        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            fx.Handler.Handle(
                new ReextractDocumentCommand(Guid.NewGuid()), CancellationToken.None));
    }

    [Theory]
    [InlineData(DocType.Other)]
    [InlineData(DocType.Cv)]
    public async Task Handle_NoExtractorForDocType_SkipsBlobLoadAndPersister(DocType docType)
    {
        // Reextract on Other / Cv has no extractor to dispatch — short-circuit
        // before pulling bytes from blob storage (avoids a needless filesystem
        // hit on documents that can't produce an extraction).
        var fx = await new Fixture().WithDocument(docType, conf: 0.97);

        var result = await fx.Handler.Handle(
            new ReextractDocumentCommand(fx.DocumentId), CancellationToken.None);

        Assert.Equal(docType, result.DocType);
        Assert.Null(result.ExtractionId);
        Assert.False(result.WasCacheHit);

        fx.Blobs.Verify(b => b.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        fx.Persister.Verify(p => p.PersistAsync(
            It.IsAny<Document>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IDocTypeExtractor>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_ThrowsArgumentException_WhenDocumentIdEmpty()
    {
        var fx = new Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fx.Handler.Handle(
                new ReextractDocumentCommand(Guid.Empty), CancellationToken.None));
    }

    // === Test fixture ====================================================

    private sealed class Fixture
    {
        public Guid DocumentId { get; } = Guid.NewGuid();
        public Mock<IBlobStore> Blobs { get; } = new(MockBehavior.Strict);
        public Mock<IExtractionPersister> Persister { get; } = new(MockBehavior.Strict);
        public PacketReadyDbContext Db { get; }
        public ReextractDocumentCommandHandler Handler { get; }

        private readonly InMemoryContextFactory _factory;

        public Fixture()
        {
            _factory = new InMemoryContextFactory(
                "reextract-tests-" + Guid.NewGuid().ToString("N"));
            Db = _factory.CreateDbContext();

            // Default mocks — the blob returns a small PDF, the persister
            // returns extractionId=7 / not a cache hit. Tests override as
            // needed via PersisterReturns().
            Blobs.Setup(b => b.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(
                    Encoding.ASCII.GetBytes("%PDF-1.4 reextract stub"),
                    writable: false));

            PersisterReturns(new ExtractionPersistResult(
                ExtractionId: 7, WasCacheHit: false, Status: ExtractionStatus.Succeeded));

            // Real keyed-service provider with a mock extractor per dispatchable
            // doc type.
            var services = new ServiceCollection();
            foreach (var docType in new[]
            {
                DocType.License, DocType.Dea, DocType.BoardCert, DocType.Malpractice,
            })
            {
                services.AddKeyedSingleton(docType, BuildExtractorMock(docType));
            }
            var sp = services.BuildServiceProvider();

            Handler = new ReextractDocumentCommandHandler(
                db: Db,
                blobs: Blobs.Object,
                persister: Persister.Object,
                services: sp,
                logger: NullLogger<ReextractDocumentCommandHandler>.Instance);
        }

        public async Task<Fixture> WithDocument(DocType docType, double conf)
        {
            var doc = Document.Create(
                providerId: Guid.NewGuid(),
                docType: docType,
                docTypeConfidence: conf,
                classifierModel: "claude-haiku-4-5",
                classifierPromptHash: new string('a', 64),
                storageUri: "file:///tmp/blob/x.pdf",
                originalName: "x.pdf",
                mimeType: "application/pdf",
                pageCount: 1,
                uploadedBy: Uploader.Provider,
                now: FixedNow);
            typeof(Document).GetProperty("Id")!.SetValue(doc, DocumentId);

            Db.Documents.Add(doc);
            await Db.SaveChangesAsync();
            return this;
        }

        public void PersisterReturns(ExtractionPersistResult result) =>
            Persister.Setup(p => p.PersistAsync(
                    It.IsAny<Document>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<IDocTypeExtractor>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);

        private static IDocTypeExtractor BuildExtractorMock(DocType docType)
        {
            var m = new Mock<IDocTypeExtractor>();
            m.SetupGet(x => x.DocType).Returns(docType);
            m.SetupGet(x => x.SchemaVersion).Returns($"{docType.ToString().ToLowerInvariant()}.v1");
            m.SetupGet(x => x.PromptResourceName).Returns($"{docType}Prompt.v1.md");
            m.SetupGet(x => x.Model).Returns("claude-sonnet-4-6");
            return m.Object;
        }
    }
}
