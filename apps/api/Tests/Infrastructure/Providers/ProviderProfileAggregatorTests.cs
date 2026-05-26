using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Providers.Exceptions;
using PacketReady.Domain.Documents;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using PacketReady.Infrastructure.Persistence;
using PacketReady.Infrastructure.Providers;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Providers;

/// <summary>
/// Branch coverage for <see cref="ProviderProfileAggregator"/>. Uses
/// <see cref="InMemoryContextFactory"/> + manually-built Document /
/// DocumentExtraction rows; the LLM call path is upstream of the aggregator
/// and not exercised here.
/// </summary>
public class ProviderProfileAggregatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AggregateAsync_HappyPath_BuildsProfileAndPopulatesProvenance()
    {
        var fx = await new Fixture()
            .WithProvider()
            .WithDocument(DocType.License,     fields: LicenseFields,     locations: LicenseLocations, confidences: LicenseConfidences)
            .WithDocument(DocType.Dea,         fields: DeaFields,         locations: DeaLocations,     confidences: DeaConfidences)
            .WithDocument(DocType.BoardCert,   fields: BoardCertFields,   locations: GenericLocations, confidences: GenericConfidences)
            .WithDocument(DocType.Malpractice, fields: MalpracticeFields, locations: GenericLocations, confidences: GenericConfidences)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        // Profile assembled from extractions; basics (NPI etc.) flow through
        // from the seeded Provider.
        Assert.Equal("Henry Anderson, MD", result.Profile.FullName);
        Assert.NotNull(result.Profile.License);
        Assert.Equal("MD-NY-99001", result.Profile.License!.Number);
        Assert.Equal("NY", result.Profile.License.State);
        Assert.Equal(LicenseStatus.Active, result.Profile.License.Status);

        Assert.NotNull(result.Profile.Dea);
        Assert.Equal("BA1234567", result.Profile.Dea!.Number);
        Assert.Equal(DeaStatus.Active, result.Profile.Dea.Status);
        Assert.Contains(DeaSchedule.II, result.Profile.Dea.Schedules);

        Assert.NotNull(result.Profile.BoardCert);
        Assert.Equal("ABIM", result.Profile.BoardCert!.Board);

        // Provenance populated for each surfaced field. The dashboard cites
        // by "license.expiryDate" and friends.
        Assert.True(result.Provenance.ContainsKey("license.expiryDate"));
        Assert.True(result.Provenance.ContainsKey("dea.deaNumber"));

        // Bbox conversion: Sonnet emits [x, y, w, h]; aggregator stores
        // X1Y1X2Y2 in raw PDF points. LicenseLocations expiryDate is
        // [120, 400, 140, 22] → expect (120, 400, 260, 422).
        var expiryProv = result.Provenance["license.expiryDate"];
        Assert.Equal(new BoundingBox(120, 400, 260, 422), expiryProv.Bbox);
        Assert.Equal(1, expiryProv.Page);
        Assert.Equal(0.95, expiryProv.Confidence);

        // No aggregator-level issues on the happy path.
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task AggregateAsync_MultipleExtractionsPerDoc_PicksLatestByExtractedAt()
    {
        // Two extractions on the same License document: the older one carries
        // an outdated licenseNumber, the newer one is the source of truth.
        // The aggregator must surface the newer extraction's fields.
        var fx = await new Fixture()
            .WithProvider()
            .WithDocumentAndTwoExtractions(DocType.License,
                olderFields: """{"fullName":"Henry Anderson, MD","licenseNumber":"OLD-NUMBER","state":"NY","issueDate":"2020-04-15","expiryDate":"2027-04-14","status":"Active"}""",
                newerFields: LicenseFields,
                olderAt: Now.AddMinutes(-30),
                newerAt: Now)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        Assert.Equal("MD-NY-99001", result.Profile.License!.Number);
    }

    [Fact]
    public async Task AggregateAsync_MissingDocument_EmitsCriticalIssue()
    {
        var fx = await new Fixture()
            .WithProvider()
            // Only license uploaded; DEA/BoardCert/Malpractice missing.
            .WithDocument(DocType.License, fields: LicenseFields, locations: LicenseLocations, confidences: LicenseConfidences)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        var missingDocIssues = result.Issues
            .Where(i => i.Validator == "DocumentStore" && i.Severity == Severity.Critical)
            .ToList();
        // Three missing doc types → three Missing-Document Critical Issues.
        // The factory uses DocType.ToWireString() (camelCase, lowercase-leading)
        // for the message body; the typed MissingDocType tag is the
        // canonical discriminator and is what the handler reads, so we
        // assert against it directly rather than substring-matching the
        // human-readable message.
        Assert.Equal(3, missingDocIssues.Count);
        Assert.Contains(missingDocIssues, i => i.MissingDocType == DocType.Dea);
        Assert.Contains(missingDocIssues, i => i.MissingDocType == DocType.BoardCert);
        Assert.Contains(missingDocIssues, i => i.MissingDocType == DocType.Malpractice);
    }

    [Fact]
    public async Task AggregateAsync_FailedExtraction_EmitsCriticalWithPersistedError()
    {
        var fx = await new Fixture()
            .WithProvider()
            .WithFailedExtraction(DocType.License, error: "PDF text layer was empty.")
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        var failed = result.Issues
            .Single(i => i.Validator == "DocumentStore"
                && i.Severity == Severity.Critical
                && i.Message.Contains("failed"));
        Assert.Contains("PDF text layer was empty.", failed.Message);
        // Citation points at the document for dashboard drill-in.
        Assert.NotNull(failed.Citations.Single().DocumentId);
    }

    [Theory]
    [InlineData(0.50)]
    [InlineData(0.70)]
    [InlineData(0.84)]
    public async Task AggregateAsync_MidConfidenceClassification_EmitsMinorIssue(double conf)
    {
        // Spec §"Classifier runtime fallback": 0.50 ≤ x < 0.85 → store the
        // predicted doc_type, surface a Minor Issue from the aggregator.
        var fx = await new Fixture()
            .WithProvider()
            .WithDocument(DocType.License, fields: LicenseFields, locations: LicenseLocations,
                confidences: LicenseConfidences, docTypeConfidence: conf)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        Assert.Contains(result.Issues, i =>
            i.Validator == "DocumentStore"
            && i.Severity == Severity.Minor
            && i.Message.Contains("low confidence"));
    }

    [Theory]
    [InlineData(0.85)]   // exact trust floor — no Minor
    [InlineData(0.95)]
    public async Task AggregateAsync_HighConfidenceClassification_EmitsNoLowConfidenceIssue(double conf)
    {
        var fx = await new Fixture()
            .WithProvider()
            .WithDocument(DocType.License, fields: LicenseFields, locations: LicenseLocations,
                confidences: LicenseConfidences, docTypeConfidence: conf)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        Assert.DoesNotContain(result.Issues, i =>
            i.Validator == "DocumentStore" && i.Message.Contains("low confidence"));
    }

    [Fact]
    public async Task AggregateAsync_CrossDocFullNameMismatch_EmitsMinor()
    {
        // License says "Henry Anderson, MD"; Dea says "Jane Smith".
        // Levenshtein distance ≫ 3 — Minor "fullName disagrees" Issue, license
        // wins for ProviderProfile.FullName per precedence chain.
        var fx = await new Fixture()
            .WithProvider()
            .WithDocument(DocType.License,
                fields: """{"fullName":"Henry Anderson, MD","licenseNumber":"X","state":"NY","issueDate":"2020-01-01","expiryDate":"2027-01-01","status":"Active"}""",
                locations: LicenseLocations,
                confidences: LicenseConfidences)
            .WithDocument(DocType.Dea,
                fields: """{"fullName":"Jane Smith","deaNumber":"BA1234567","expiryDate":"2027-08-31","status":"Active","schedules":["II"]}""",
                locations: DeaLocations,
                confidences: DeaConfidences)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        Assert.Equal("Henry Anderson, MD", result.Profile.FullName);
        Assert.Contains(result.Issues, i =>
            i.Validator == "DocumentStore"
            && i.Severity == Severity.Minor
            && i.Message.Contains("fullName disagrees"));
    }

    [Fact]
    public async Task AggregateAsync_StackedCredentials_NormalizeStripsToFixpoint()
    {
        // "Henry Anderson, MD, PhD" vs "Henry Anderson" — single-pass stripping
        // would leave ", MD" behind once ", PhD" stripped; the fixpoint loop
        // must reduce both. Levenshtein on the normalized forms is 0 → no Minor.
        var fx = await new Fixture()
            .WithProvider()
            .WithDocument(DocType.License,
                fields: """{"fullName":"Henry Anderson, MD, PhD","licenseNumber":"X","state":"NY","issueDate":"2020-01-01","expiryDate":"2027-01-01","status":"Active"}""",
                locations: LicenseLocations,
                confidences: LicenseConfidences)
            .WithDocument(DocType.Dea,
                fields: """{"fullName":"Henry Anderson","deaNumber":"BA1234567","expiryDate":"2027-08-31","status":"Active","schedules":[]}""",
                locations: DeaLocations,
                confidences: DeaConfidences)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        Assert.DoesNotContain(result.Issues, i =>
            i.Message.Contains("fullName disagrees"));
    }

    [Fact]
    public async Task AggregateAsync_PartialExtraction_EmitsCriticalAndProfileFieldStaysNull()
    {
        // Extraction succeeded but the parsed-required `expiryDate` is absent —
        // ParseLicense returns null, the aggregator emits a Partial-Extraction
        // Critical, and the validator (running downstream) stays silent for
        // the missing license. This is the lane that justifies the validator
        // short-circuit on profile.License is null.
        var fx = await new Fixture()
            .WithProvider()
            .WithDocument(DocType.License,
                fields: """{"fullName":"Henry Anderson, MD","licenseNumber":"X","state":"NY","issueDate":"2020-01-01","status":"Active"}""",
                locations: LicenseLocations,
                confidences: LicenseConfidences)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        Assert.Null(result.Profile.License);
        Assert.Contains(result.Issues, i =>
            i.Validator == "DocumentStore"
            && i.Severity == Severity.Critical
            && i.Message.Contains("required fields are missing"));
    }

    [Fact]
    public async Task AggregateAsync_SmallNameDelta_DoesNotEmitMismatchMinor()
    {
        // "Henry Anderson" vs "Henry Anderson, MD" — Levenshtein 4 (", MD"
        // adds 4 chars). With the threshold at ≥ 3 this DOES fire. Use a
        // 1-char delta instead to test the suppression side.
        var fx = await new Fixture()
            .WithProvider()
            .WithDocument(DocType.License,
                fields: """{"fullName":"Henry Anderson","licenseNumber":"X","state":"NY","issueDate":"2020-01-01","expiryDate":"2027-01-01","status":"Active"}""",
                locations: LicenseLocations,
                confidences: LicenseConfidences)
            .WithDocument(DocType.Dea,
                fields: """{"fullName":"Henry Andersen","deaNumber":"BA1234567","expiryDate":"2027-08-31","status":"Active","schedules":[]}""",
                locations: DeaLocations,
                confidences: DeaConfidences)
            .BuildAsync();

        var result = await fx.Aggregator.AggregateAsync(fx.ProviderId, default);

        // Distance 1 (o→e) — below the 3 threshold; suppressed.
        Assert.DoesNotContain(result.Issues, i =>
            i.Message.Contains("fullName disagrees"));
    }

    [Fact]
    public async Task AggregateAsync_ProviderNotFound_Throws()
    {
        var fx = await new Fixture().BuildAsync();

        await Assert.ThrowsAsync<ProviderNotFoundException>(() =>
            fx.Aggregator.AggregateAsync(Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData("", "kitten", 6)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("abc", "abc", 0)]
    public void Levenshtein_KnownPairs(string a, string b, int expected)
    {
        Assert.Equal(expected, ProviderProfileAggregator.Levenshtein(a, b));
    }

    // === Test fixtures ==================================================

    private const string LicenseFields = """{"fullName":"Henry Anderson, MD","licenseNumber":"MD-NY-99001","state":"NY","issueDate":"2020-04-15","expiryDate":"2027-04-14","status":"Active"}""";
    private const string LicenseLocations = """{"fullName":{"page":1,"bbox":[120,240,380,22]},"licenseNumber":{"page":1,"bbox":[120,280,200,22]},"state":{"page":1,"bbox":[120,320,60,22]},"issueDate":{"page":1,"bbox":[120,360,140,22]},"expiryDate":{"page":1,"bbox":[120,400,140,22]},"status":{"page":1,"bbox":[120,440,100,22]}}""";
    private const string LicenseConfidences = """{"fullName":0.97,"licenseNumber":0.98,"state":0.99,"issueDate":0.95,"expiryDate":0.95,"status":0.93}""";

    private const string DeaFields = """{"fullName":"Henry Anderson","deaNumber":"BA1234567","expiryDate":"2027-08-31","status":"Active","schedules":["II","III","IV","V"]}""";
    private const string DeaLocations = """{"fullName":{"page":1,"bbox":[120,240,380,22]},"deaNumber":{"page":1,"bbox":[120,280,200,22]},"expiryDate":{"page":1,"bbox":[120,320,140,22]},"status":{"page":1,"bbox":[120,360,100,22]},"schedules":{"page":1,"bbox":[120,400,200,22]}}""";
    private const string DeaConfidences = """{"fullName":0.97,"deaNumber":0.99,"expiryDate":0.95,"status":0.93,"schedules":0.96}""";

    private const string BoardCertFields = """{"fullName":"Henry Anderson, MD","board":"ABIM","specialty":"Internal Medicine","issueDate":"2018-06-01","expiryDate":"2028-06-01","status":"Active"}""";
    private const string MalpracticeFields = """{"fullName":"Henry Anderson, MD","carrier":"MedProtect Mutual","policyNumber":"MPM-NY-00099001","expiryDate":"2026-12-31","status":"Active"}""";
    private const string GenericLocations = """{"fullName":{"page":1,"bbox":[1,1,1,1]}}""";
    private const string GenericConfidences = """{"fullName":0.95}""";

    private sealed class Fixture
    {
        public Guid ProviderId { get; private set; }
        public PacketReadyDbContext Db { get; }
        public ProviderProfileAggregator Aggregator { get; }

        private readonly InMemoryContextFactory _factory;

        public Fixture()
        {
            _factory = new InMemoryContextFactory(
                "aggregator-tests-" + Guid.NewGuid().ToString("N"));
            Db = _factory.CreateDbContext();
            Aggregator = new ProviderProfileAggregator(
                Db, NullLogger<ProviderProfileAggregator>.Instance);
        }

        public Fixture WithProvider()
        {
            var profile = ProviderProfile.Create(
                fullName: "Pre-Extraction Name",
                dateOfBirth: new DateOnly(1980, 1, 1),
                npi: "1234567890",
                credentialingState: "NY",
                nowUtc: Now);
            var provider = Provider.Create(profile, Now);
            ProviderId = provider.Id;
            Db.Providers.Add(provider);
            return this;
        }

        public Fixture WithDocument(
            DocType docType,
            string fields, string locations, string confidences,
            double docTypeConfidence = 0.95)
        {
            var doc = Document.Create(
                providerId: ProviderId,
                docType: docType,
                docTypeConfidence: docTypeConfidence,
                classifierModel: "claude-haiku-4-5",
                classifierPromptHash: new string('a', 64),
                storageUri: "file:///tmp/x.pdf",
                originalName: $"{docType}.pdf",
                mimeType: "application/pdf",
                pageCount: 1,
                uploadedBy: Uploader.Provider,
                now: Now);

            var ext = DocumentExtraction.CreateLlmSucceeded(
                documentId: doc.Id,
                extractionId: 1,
                schemaVersion: $"{docType.ToString().ToLowerInvariant()}.v1",
                fieldsJson: fields,
                fieldLocationsJson: locations,
                confidenceJson: confidences,
                model: "claude-sonnet-4-6",
                promptHash: new string('b', 64),
                inputTokens: 5000,
                outputTokens: 400,
                now: Now);

            Db.Documents.Add(doc);
            Db.DocumentExtractions.Add(ext);
            return this;
        }

        public Fixture WithDocumentAndTwoExtractions(
            DocType docType,
            string olderFields, string newerFields,
            DateTimeOffset olderAt, DateTimeOffset newerAt)
        {
            var doc = Document.Create(
                providerId: ProviderId,
                docType: docType,
                docTypeConfidence: 0.95,
                classifierModel: "claude-haiku-4-5",
                classifierPromptHash: new string('a', 64),
                storageUri: "file:///tmp/x.pdf",
                originalName: $"{docType}.pdf",
                mimeType: "application/pdf",
                pageCount: 1,
                uploadedBy: Uploader.Provider,
                now: Now);
            Db.Documents.Add(doc);

            // extractionId increments per document; both rows distinct under
            // the (document_id, extraction_id) UNIQUE.
            Db.DocumentExtractions.Add(DocumentExtraction.CreateLlmSucceeded(
                documentId: doc.Id, extractionId: 1,
                schemaVersion: $"{docType.ToString().ToLowerInvariant()}.v1",
                fieldsJson: olderFields,
                fieldLocationsJson: LicenseLocations,
                confidenceJson: LicenseConfidences,
                model: "claude-sonnet-4-6",
                promptHash: new string('b', 64),
                inputTokens: 5000, outputTokens: 400,
                now: olderAt));
            Db.DocumentExtractions.Add(DocumentExtraction.CreateLlmSucceeded(
                documentId: doc.Id, extractionId: 2,
                schemaVersion: $"{docType.ToString().ToLowerInvariant()}.v1",
                fieldsJson: newerFields,
                fieldLocationsJson: LicenseLocations,
                confidenceJson: LicenseConfidences,
                model: "claude-sonnet-4-6",
                promptHash: new string('b', 64),
                inputTokens: 5000, outputTokens: 400,
                now: newerAt));
            return this;
        }

        public Fixture WithFailedExtraction(DocType docType, string error)
        {
            var doc = Document.Create(
                providerId: ProviderId,
                docType: docType,
                docTypeConfidence: 0.95,
                classifierModel: "claude-haiku-4-5",
                classifierPromptHash: new string('a', 64),
                storageUri: "file:///tmp/x.pdf",
                originalName: $"{docType}.pdf",
                mimeType: "application/pdf",
                pageCount: 1,
                uploadedBy: Uploader.Provider,
                now: Now);

            var ext = DocumentExtraction.CreateLlmFailed(
                documentId: doc.Id,
                extractionId: 1,
                schemaVersion: $"{docType.ToString().ToLowerInvariant()}.v1",
                error: error,
                model: "claude-sonnet-4-6",
                promptHash: new string('b', 64),
                now: Now);

            Db.Documents.Add(doc);
            Db.DocumentExtractions.Add(ext);
            return this;
        }

        public async Task<Fixture> BuildAsync()
        {
            await Db.SaveChangesAsync();
            return this;
        }
    }
}
