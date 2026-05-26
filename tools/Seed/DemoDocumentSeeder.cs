using System.Text.Json;
using PacketReady.Application.Audit;
using PacketReady.Application.Audit.Payloads;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Documents;
using PacketReady.Domain.Providers;
using PacketReady.Infrastructure.Persistence;

namespace PacketReady.Seed;

/// <summary>
/// `--demo` mode plumbing for the document + extraction lane. P4 moved the
/// score handler off Provider.profile and onto the documents/extractions
/// pipeline, so the demo seed has to materialize that lane synthetically:
/// 4 documents (license / dea / boardCert / malpractice) per provider with
/// blob storage backing real PDFs from <c>evals/dataset/packet-001-clean-anderson/</c>,
/// extraction rows whose fields JSONB mirrors the fixture profile, and
/// DocumentUploaded audit events so beat 3 of the recording shows a populated
/// timeline.
///
/// <para>The same source PDFs are reused for all 3 demo providers — the
/// blob ids are unique, but the file contents repeat. Recording-day
/// cosmetics; nobody pixel-peeps the PDF preview during the side panel
/// open.</para>
/// </summary>
internal static class DemoDocumentSeeder
{
    private const string DemoClassifierModel = "demo-seed";
    private const string DemoClassifierPromptHash = "demo-seed-classifier";
    private const string DemoExtractionModel = "demo-seed";
    private const string DemoExtractionPromptHash = "demo-seed-extraction";
    private const double DemoClassifierConfidence = 0.98;
    private const double DemoFieldConfidence = 0.97;

    public static async Task SeedAsync(
        PacketReadyDbContext db,
        IAuditWriter audit,
        Provider provider,
        ProviderProfile profile,
        DateTimeOffset nowUtc,
        string blobStoreRoot,
        string sourcePdfDir,
        CancellationToken ct)
    {
        // Per-doc-type tuple driving the inserts. Each step is independent;
        // the loop just keeps the four calls visually next to each other.
        var docPlan = new (DocType DocType, string SourceFile, Func<(string fields, string locs, string confs)> Builder)[]
        {
            (DocType.License, "license.pdf", () => BuildLicense(profile)),
            (DocType.Dea, "dea.pdf", () => BuildDea(profile)),
            (DocType.BoardCert, "board-cert.pdf", () => BuildBoardCert(profile)),
            (DocType.Malpractice, "malpractice.pdf", () => BuildMalpractice(profile)),
        };

        var occurredAt = nowUtc;
        foreach (var (docType, sourceFile, builder) in docPlan)
        {
            var (fields, locs, confs) = builder();

            var storageUri = await CopyToBlobStoreAsync(
                sourcePath: Path.Combine(sourcePdfDir, sourceFile),
                blobStoreRoot: blobStoreRoot,
                nowUtc: nowUtc,
                ct: ct);

            var document = Document.Create(
                providerId: provider.Id,
                docType: docType,
                docTypeConfidence: DemoClassifierConfidence,
                classifierModel: DemoClassifierModel,
                classifierPromptHash: DemoClassifierPromptHash,
                storageUri: storageUri,
                originalName: sourceFile,
                mimeType: "application/pdf",
                pageCount: 1,
                uploadedBy: Uploader.Admin,
                now: occurredAt);
            db.Documents.Add(document);

            var extraction = DocumentExtraction.CreateLlmSucceeded(
                documentId: document.Id,
                extractionId: 1,
                schemaVersion: SchemaVersionFor(docType),
                fieldsJson: fields,
                fieldLocationsJson: locs,
                confidenceJson: confs,
                model: DemoExtractionModel,
                promptHash: DemoExtractionPromptHash,
                inputTokens: 0,
                outputTokens: 0,
                now: occurredAt);
            db.DocumentExtractions.Add(extraction);

            // DocumentUploaded payload mirrors UploadDocumentCommandHandler's
            // shape so the dashboard's audit-trail PayloadSummary renders the
            // same chip. `docTypeConfidence` is the key the dashboard reads
            // (see apps/dashboard/components/audit-trail.tsx PayloadSummary).
            var payload = JsonSerializer.Serialize(new
            {
                documentId = document.Id,
                docType = docType.ToWireString(),
                docTypeConfidence = DemoClassifierConfidence,
                classifierConfidence = DemoClassifierConfidence,
                classifierRationale = "Demo seed — bypasses real classifier.",
                storageUri,
                pageCount = 1,
            });
            audit.Stage(AuditEvent.Create(
                AuditEventType.DocumentUploaded,
                payload,
                providerId: provider.Id,
                occurredAt: occurredAt));

            // Tick each event forward by a second so the audit-trail row
            // ordering reads as "license, then dea, then ..." in beat 3.
            occurredAt = occurredAt.AddSeconds(1);
        }

        await db.SaveChangesAsync(ct);
    }

    private static string SchemaVersionFor(DocType docType) => docType switch
    {
        DocType.License => "license.v2",
        DocType.Dea => "dea.v1",
        DocType.BoardCert => "boardCert.v1",
        DocType.Malpractice => "malpractice.v2",
        _ => throw new InvalidOperationException($"No schema version for {docType}."),
    };

    private static async Task<string> CopyToBlobStoreAsync(
        string sourcePath,
        string blobStoreRoot,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                $"Demo seed: source PDF not found at {sourcePath}. Run from repo root.");

        // Mirror LocalFileBlobStore's yyyy/MM shard so the resulting URI is
        // resolvable by the running API without further coordination.
        var shard = Path.Combine(blobStoreRoot, nowUtc.ToString("yyyy"), nowUtc.ToString("MM"));
        Directory.CreateDirectory(shard);

        var id = Guid.NewGuid();
        var dest = Path.Combine(shard, $"{id:N}.pdf");
        File.Copy(sourcePath, dest, overwrite: false);

        return new Uri(dest).AbsoluteUri;
    }

    // === Per-doc-type JSONB builders ===================================
    //
    // Each builder emits the (fields, fieldLocations, confidences) triple
    // the aggregator's parser expects. Bounding boxes are plausible PDF-points
    // rectangles (top-left origin, width×height) — the dashboard's bbox overlay
    // renders against the rendered PDF page so the absolute pixel values don't
    // matter, only that they fall inside [0, page-size].

    // NOTE: per-doc `fullName` is intentionally omitted from every builder.
    // Including it triggers IdentityCoherenceValidator (gates on >=2 per-doc
    // names) and NpiTaxonomyMatchValidator (gates on taxonomyCode) into a
    // real Anthropic round-trip — which the seed CLI is wired with a noop
    // IChatClient that throws on any actual call. The aggregator falls back
    // to Provider.profile.FullName when no per-doc names are present, so
    // dashboard rendering is unchanged.

    private static (string fields, string locs, string confs) BuildLicense(ProviderProfile p)
    {
        var lic = p.License ?? throw new InvalidOperationException("Demo seed: license missing from profile.");
        var fields = JsonSerializer.Serialize(new
        {
            licenseNumber = lic.Number,
            state = lic.State,
            issueDate = lic.IssueDate.ToString("yyyy-MM-dd"),
            expiryDate = lic.ExpiryDate.ToString("yyyy-MM-dd"),
            status = lic.Status.ToString(),
        });
        var locs = BuildLocations(["licenseNumber", "state", "issueDate", "expiryDate", "status"]);
        var confs = BuildConfidences(["licenseNumber", "state", "issueDate", "expiryDate", "status"]);
        return (fields, locs, confs);
    }

    private static (string fields, string locs, string confs) BuildDea(ProviderProfile p)
    {
        var dea = p.Dea ?? throw new InvalidOperationException("Demo seed: dea missing from profile.");
        var fields = JsonSerializer.Serialize(new
        {
            deaNumber = dea.Number,
            expiryDate = dea.ExpiryDate.ToString("yyyy-MM-dd"),
            status = dea.Status.ToString(),
            schedules = dea.Schedules.Select(s => s.ToString()).ToArray(),
        });
        var locs = BuildLocations(["deaNumber", "expiryDate", "status"]);
        var confs = BuildConfidences(["deaNumber", "expiryDate", "status"]);
        return (fields, locs, confs);
    }

    private static (string fields, string locs, string confs) BuildBoardCert(ProviderProfile p)
    {
        var bc = p.BoardCert ?? throw new InvalidOperationException("Demo seed: boardCert missing from profile.");
        var fields = JsonSerializer.Serialize(new
        {
            board = bc.Board,
            specialty = bc.Specialty,
            issueDate = bc.IssueDate.ToString("yyyy-MM-dd"),
            expiryDate = bc.ExpiryDate.ToString("yyyy-MM-dd"),
            status = bc.Status.ToString(),
        });
        var locs = BuildLocations(["board", "specialty", "issueDate", "expiryDate", "status"]);
        var confs = BuildConfidences(["board", "specialty", "issueDate", "expiryDate", "status"]);
        return (fields, locs, confs);
    }

    private static (string fields, string locs, string confs) BuildMalpractice(ProviderProfile p)
    {
        var mp = p.Malpractice ?? throw new InvalidOperationException("Demo seed: malpractice missing from profile.");
        var fields = JsonSerializer.Serialize(new
        {
            carrier = mp.Carrier,
            policyNumber = mp.PolicyNumber,
            expiryDate = mp.ExpiryDate.ToString("yyyy-MM-dd"),
            status = mp.Status.ToString(),
            perOccurrence = mp.PerOccurrence,
            aggregate = mp.Aggregate,
        });
        var locs = BuildLocations(["carrier", "policyNumber", "expiryDate", "status", "perOccurrence", "aggregate"]);
        var confs = BuildConfidences(["carrier", "policyNumber", "expiryDate", "status", "perOccurrence", "aggregate"]);
        return (fields, locs, confs);
    }

    private static string BuildLocations(string[] fieldNames)
    {
        // Stack fields vertically down the page at 60pt intervals. Width
        // 400pt, height 24pt. Lands inside a typical 612×792 letter page.
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < fieldNames.Length; i++)
        {
            var y = 120 + i * 60;
            map[fieldNames[i]] = new
            {
                page = 1,
                bbox = new[] { 80, y, 400, 24 },
            };
        }
        return JsonSerializer.Serialize(map);
    }

    private static string BuildConfidences(string[] fieldNames)
    {
        var map = fieldNames.ToDictionary(f => f, _ => DemoFieldConfidence, StringComparer.Ordinal);
        return JsonSerializer.Serialize(map);
    }
}
