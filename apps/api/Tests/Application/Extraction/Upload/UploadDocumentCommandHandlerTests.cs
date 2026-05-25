using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PacketReady.Application.Audit;
using PacketReady.Application.Documents;
using PacketReady.Application.Extraction.Classify;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Application.Extraction.Persist;
using PacketReady.Application.Extraction.Upload;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Documents;
using PacketReady.Domain.Providers;
using PacketReady.Infrastructure.Persistence;
using PacketReady.Tests.Infrastructure;
using Xunit;

namespace PacketReady.Tests.Application.Extraction.Upload;

/// <summary>
/// Branch coverage for <see cref="UploadDocumentCommandHandler"/>. Uses an
/// EF InMemory <see cref="PacketReadyDbContext"/> + Moqs of every other
/// dependency. The persister is mocked too — its own transaction/advisory-lock
/// dance is exercised by the migration-smoke SQL fixtures and the live-LLM
/// integration tests, not here.
/// </summary>
public class UploadDocumentCommandHandlerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_HappyPath_PersistsDocumentAndExtractionRow()
    {
        var fixture = await new Fixture().WithExistingProvider();
        var stagedAudit = fixture.CaptureStagedAuditEvent();

        var result = await fixture.Handler.Handle(
            new UploadDocumentCommand(
                ProviderId: fixture.ProviderId,
                PdfBytes: SamplePdfBytes,
                OriginalName: "license.pdf",
                MimeType: "application/pdf",
                PageCount: 3,
                UploadedBy: Uploader.Provider),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.DocumentId);
        Assert.Equal(DocType.License, result.DocType);
        Assert.Equal(0.95, result.DocTypeConfidence);
        Assert.Equal(1, result.ExtractionId);
        Assert.False(result.WasCacheHit);

        // Document row landed with classifier provenance + storage uri.
        var doc = await fixture.ReadDocuments().SingleAsync();
        Assert.Equal(fixture.ProviderId, doc.ProviderId);
        Assert.Equal(DocType.License, doc.DocType);
        Assert.Equal(0.95, doc.DocTypeConfidence);
        Assert.Equal("claude-haiku-4-5", doc.ClassifierModel);
        Assert.StartsWith("file://", doc.StorageUri);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(Uploader.Provider, doc.UploadedBy);

        // DocumentUploaded audit event staged with the full payload shape the
        // dashboard will read. Asserting on the JSON contents (not just the
        // call interaction) catches payload-field renames silently.
        Assert.NotNull(stagedAudit.Value);
        var evt = stagedAudit.Value!;
        Assert.Equal(AuditEventType.DocumentUploaded, evt.EventType);
        Assert.Equal(fixture.ProviderId, evt.ProviderId);

        using var payload = JsonDocument.Parse(evt.Payload);
        var root = payload.RootElement;
        Assert.Equal(result.DocumentId, root.GetProperty("documentId").GetGuid());
        Assert.Equal("license", root.GetProperty("docType").GetString());
        Assert.Equal(0.95, root.GetProperty("classifierConfidence").GetDouble());
        Assert.Equal("license title + state seal", root.GetProperty("classifierRationale").GetString());
        Assert.Equal(doc.StorageUri, root.GetProperty("storageUri").GetString());
        Assert.Equal(3, root.GetProperty("pageCount").GetInt32());

        fixture.Persister.Verify(
            p => p.PersistAsync(It.IsAny<Document>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IDocTypeExtractor>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_ThrowsProviderNotFound_WhenProviderMissing()
    {
        var fixture = new Fixture();   // no provider seeded

        await Assert.ThrowsAsync<ProviderNotFoundException>(() =>
            fixture.Handler.Handle(
                new UploadDocumentCommand(
                    ProviderId: Guid.NewGuid(),
                    PdfBytes: SamplePdfBytes,
                    OriginalName: "license.pdf",
                    MimeType: "application/pdf",
                    PageCount: 1,
                    UploadedBy: Uploader.Provider),
                CancellationToken.None));
    }

    [Fact]
    public async Task Handle_RejectsEmptyPdf()
    {
        var fixture = await new Fixture().WithExistingProvider();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Handler.Handle(
                new UploadDocumentCommand(
                    ProviderId: fixture.ProviderId,
                    PdfBytes: Array.Empty<byte>(),
                    OriginalName: "license.pdf",
                    MimeType: "application/pdf",
                    PageCount: 1,
                    UploadedBy: Uploader.Provider),
                CancellationToken.None));
    }

    [Fact]
    public async Task Handle_LowConfidenceClassifier_StoresDocTypeAsOther_AndSkipsExtractor()
    {
        // Spec §"Classifier runtime fallback": < 0.50 → persist as Other.
        // The model still predicted "license" but we override to Other and
        // skip the extractor — aggregator will surface the doc separately.
        var fixture = await new Fixture()
            .WithClassifierResult(DocType.License, 0.40, "low-quality scan")
            .WithExistingProvider();

        var result = await fixture.Handler.Handle(
            new UploadDocumentCommand(
                ProviderId: fixture.ProviderId,
                PdfBytes: SamplePdfBytes,
                OriginalName: "license.pdf",
                MimeType: "application/pdf",
                PageCount: 1,
                UploadedBy: Uploader.Provider),
            CancellationToken.None);

        Assert.Equal(DocType.Other, result.DocType);
        Assert.Equal(0.40, result.DocTypeConfidence);
        Assert.Null(result.ExtractionId);

        var doc = await fixture.ReadDocuments().SingleAsync();
        Assert.Equal(DocType.Other, doc.DocType);

        fixture.Persister.Verify(
            p => p.PersistAsync(It.IsAny<Document>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IDocTypeExtractor>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0.50)]   // exact band floor — predicted preserved
    [InlineData(0.65)]   // mid-band
    [InlineData(0.84)]   // just under the trust ceiling
    [InlineData(0.85)]   // exact trust-band floor — pinned so a `<` → `<=` flip is caught
    [InlineData(1.00)]   // trust ceiling
    public async Task Handle_MidOrHighConfidenceClassifier_PreservesPredictedDocType(double confidence)
    {
        // 0.50 ≤ x < 0.85: predicted doc_type is persisted as-is. Aggregator
        // emits the Minor Issue in slice 8; the handler doesn't decide that.
        var fixture = await new Fixture()
            .WithClassifierResult(DocType.License, confidence, "title visible, layout partial")
            .WithExistingProvider();

        var result = await fixture.Handler.Handle(
            new UploadDocumentCommand(
                ProviderId: fixture.ProviderId,
                PdfBytes: SamplePdfBytes,
                OriginalName: "license.pdf",
                MimeType: "application/pdf",
                PageCount: 1,
                UploadedBy: Uploader.Provider),
            CancellationToken.None);

        Assert.Equal(DocType.License, result.DocType);
        Assert.Equal(confidence, result.DocTypeConfidence);
        Assert.Equal(1, result.ExtractionId);
        fixture.Persister.Verify(p => p.PersistAsync(
            It.IsAny<Document>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<IDocTypeExtractor>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_ClassifierReturnsCv_SkipsExtractor()
    {
        // CV is the only doc type the classifier can confidently identify but
        // we don't yet have an extractor for (P4 territory). Persist the
        // Document row so the dashboard sees the upload; skip extraction.
        var fixture = await new Fixture()
            .WithClassifierResult(DocType.Cv, 0.97, "resume layout")
            .WithExistingProvider();

        var result = await fixture.Handler.Handle(
            new UploadDocumentCommand(
                ProviderId: fixture.ProviderId,
                PdfBytes: SamplePdfBytes,
                OriginalName: "cv.pdf",
                MimeType: "application/pdf",
                PageCount: 2,
                UploadedBy: Uploader.Provider),
            CancellationToken.None);

        Assert.Equal(DocType.Cv, result.DocType);
        Assert.Equal(0.97, result.DocTypeConfidence);
        Assert.Null(result.ExtractionId);

        var doc = await fixture.ReadDocuments().SingleAsync();
        Assert.Equal(DocType.Cv, doc.DocType);

        fixture.Persister.Verify(
            p => p.PersistAsync(It.IsAny<Document>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IDocTypeExtractor>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // === Test fixture ====================================================

    private static readonly byte[] SamplePdfBytes =
        System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 test stub");

    private sealed class Fixture
    {
        // Populated by WithExistingProvider() from the persisted entity's real
        // Id. Stays Guid.Empty when no provider is seeded (e.g. the not-found
        // test, which passes its own Guid directly).
        public Guid ProviderId { get; private set; }
        public Mock<IBlobStore> Blobs { get; } = new(MockBehavior.Strict);
        public Mock<IDocumentClassifier> Classifier { get; } = new(MockBehavior.Strict);
        public Mock<IExtractionPersister> Persister { get; } = new(MockBehavior.Strict);
        public Mock<IAuditWriter> Audit { get; } = new(MockBehavior.Loose);
        public PacketReadyDbContext Db { get; }
        public UploadDocumentCommandHandler Handler { get; }

        private readonly InMemoryContextFactory _factory;

        public Fixture()
        {
            _factory = new InMemoryContextFactory(
                "upload-tests-" + Guid.NewGuid().ToString("N"));
            Db = _factory.CreateDbContext();

            // Default mocks — overridable via With*() chain.
            Blobs.Setup(b => b.PutAsync(
                    It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("file:///tmp/blob/x.pdf");

            Classifier.Setup(c => c.ClassifyAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ClassificationResult(
                    DocType: DocType.License,
                    Confidence: 0.95,
                    Rationale: "license title + state seal",
                    Model: "claude-haiku-4-5",
                    PromptHash: new string('a', 64),
                    InputTokens: 3500,
                    OutputTokens: 80));

            Persister.Setup(p => p.PersistAsync(
                    It.IsAny<Document>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<IDocTypeExtractor>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExtractionPersistResult(
                    ExtractionId: 1,
                    WasCacheHit: false,
                    Status: ExtractionStatus.Succeeded));

            // Real keyed-service provider — register a Moqed extractor per
            // doc-type so GetRequiredKeyedService doesn't throw.
            var services = new ServiceCollection();
            foreach (var docType in new[]
            {
                DocType.License, DocType.Dea, DocType.BoardCert, DocType.Malpractice,
            })
            {
                services.AddKeyedSingleton(docType, BuildExtractorMock(docType));
            }
            var sp = services.BuildServiceProvider();

            Handler = new UploadDocumentCommandHandler(
                db: Db,
                blobs: Blobs.Object,
                classifier: Classifier.Object,
                persister: Persister.Object,
                audit: Audit.Object,
                services: sp,
                time: new FakeTimeProvider(FixedNow),
                logger: NullLogger<UploadDocumentCommandHandler>.Instance);
        }

        public async Task<Fixture> WithExistingProvider()
        {
            var profile = ProviderProfile.Create(
                fullName: "Test Provider",
                dateOfBirth: new DateOnly(1980, 1, 1),
                npi: "1234567890",
                credentialingState: "NY",
                nowUtc: FixedNow);
            var provider = Provider.Create(profile, FixedNow);
            Db.Providers.Add(provider);
            await Db.SaveChangesAsync();
            // Capture the entity's real Id — Provider.Create assigns Guid.NewGuid
            // and there's no test-only override factory. Avoids reflection-based
            // private-setter hacks that break silently on field rename.
            ProviderId = provider.Id;
            return this;
        }

        public Fixture WithClassifierResult(DocType docType, double confidence, string rationale)
        {
            Classifier.Setup(c => c.ClassifyAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ClassificationResult(
                    DocType: docType,
                    Confidence: confidence,
                    Rationale: rationale,
                    Model: "claude-haiku-4-5",
                    PromptHash: new string('a', 64),
                    InputTokens: 3500,
                    OutputTokens: 80));
            return this;
        }

        // Reads through a fresh context bound to the same InMemory database —
        // catches cases where the handler held tracked entities back from
        // commit.
        public IQueryable<Document> ReadDocuments() => _factory.CreateDbContext().Documents;

        // Hook for tests that need to assert on the staged AuditEvent's payload
        // shape. Returns a holder whose .Value is populated by the handler call.
        // (Setup + Callback because the audit mock is Loose; Invocations would
        // also work but is more fiddly to read.)
        public StagedAuditHolder CaptureStagedAuditEvent()
        {
            var holder = new StagedAuditHolder();
            Audit.Setup(a => a.Stage(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(e => holder.Value = e)
                .Returns(Guid.NewGuid());
            return holder;
        }

        private static IDocTypeExtractor BuildExtractorMock(DocType docType)
        {
            var m = new Mock<IDocTypeExtractor>();
            m.SetupGet(x => x.DocType).Returns(docType);
            m.SetupGet(x => x.SchemaVersion).Returns($"{docType.ToString().ToLowerInvariant()}.v1");
            m.SetupGet(x => x.PromptResourceName).Returns($"{docType}Prompt.v1.md");
            m.SetupGet(x => x.Model).Returns("claude-sonnet-4-6");
            return m.Object;
        }

        public sealed class StagedAuditHolder
        {
            public AuditEvent? Value { get; set; }
        }
    }
}
