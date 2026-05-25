using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;
using PacketReady.Domain.Documents;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using PacketReady.Infrastructure.Persistence;

namespace PacketReady.Infrastructure.Providers;

/// <summary>
/// Postgres-backed <see cref="IProviderProfileAggregator"/>. Reads the latest
/// succeeded extraction per <c>(provider_id, doc_type)</c>, maps each doc-type
/// JSONB into the matching <c>*Info</c> record, and assembles a
/// <see cref="ProviderProfile"/> by overlaying extracted credential rows on
/// top of the Provider row's hand-curated basics (NPI, DOB, state).
///
/// <para>P3 doesn't extract NPI/DOB/state from any doc type; those still come
/// from the Provider's existing <c>profile</c> JSONB. P4's CV extractor (not
/// shipped) will populate them — at that point the aggregator becomes
/// extraction-only and the Provider's stored profile becomes redundant.</para>
/// </summary>
internal sealed class ProviderProfileAggregator : IProviderProfileAggregator
{
    // Doc types we expect every provider to have. Missing one => Critical
    // Missing-Document Issue. The order is the cross-doc fullName precedence
    // chain (license wins ties when the dashboard shows ProviderProfile.FullName).
    private static readonly DocType[] ExpectedDocTypes =
    {
        DocType.License, DocType.Dea, DocType.BoardCert, DocType.Malpractice,
    };

    // 0.50–0.85 — classifier got a doc-type but with mid-band confidence.
    // Aggregator emits a Minor "low-confidence classification" Issue per spec
    // §"Classifier runtime fallback".
    private const double LowConfidenceFloor = 0.50;
    private const double TrustConfidenceFloor = 0.85;

    // Levenshtein threshold for cross-doc fullName mismatch. Below this is
    // typo-or-suffix noise ("Henry Anderson" vs "Henry Anderson, MD"); at or
    // above is a real disagreement worth surfacing as a Minor.
    private const int FullNameLevenshteinFloor = 3;

    private readonly PacketReadyDbContext _db;
    private readonly ILogger<ProviderProfileAggregator> _logger;

    public ProviderProfileAggregator(
        PacketReadyDbContext db,
        ILogger<ProviderProfileAggregator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AggregatedProfile> AggregateAsync(Guid providerId, CancellationToken ct)
    {
        if (providerId == Guid.Empty)
            throw new ArgumentException("Provider id is required.", nameof(providerId));

        // Provider basics — the Provider's hand-curated profile from P1 is the
        // source of truth for NPI/DOB/state until P4's CV extractor lands.
        var provider = await _db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new ProviderNotFoundException(providerId);
        var basics = provider.GetProfile();

        // Latest document per (provider, dispatchable doc_type). Other/Cv/null
        // are skipped — aggregator builds nothing from them.
        var dispatchableDocs = await _db.Documents
            .AsNoTracking()
            .Where(d => d.ProviderId == providerId
                && d.DocType != null
                && ExpectedDocTypes.Contains(d.DocType.Value))
            .ToListAsync(ct);

        var latestByDocType = dispatchableDocs
            .GroupBy(d => d.DocType!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.UploadedAt).First());

        // For each latest document, pull its latest succeeded (or Failed)
        // extraction by ExtractedAt. One round-trip per doc keeps the query
        // simple; we expect ≤ 4 docs per provider in P3.
        var extractionsByDocId = new Dictionary<Guid, DocumentExtraction>();
        foreach (var doc in latestByDocType.Values)
        {
            var ext = await _db.DocumentExtractions
                .AsNoTracking()
                .Where(e => e.DocumentId == doc.Id)
                .OrderByDescending(e => e.ExtractedAt)
                .FirstOrDefaultAsync(ct);
            if (ext is not null)
                extractionsByDocId[doc.Id] = ext;
        }

        var provenance = new Dictionary<string, FieldProvenance>(StringComparer.Ordinal);
        var issues = new List<Issue>();

        LicenseInfo? license = basics.License;
        DeaInfo? dea = basics.Dea;
        BoardCertInfo? boardCert = basics.BoardCert;
        var nameCandidates = new List<(DocType Source, string FullName)>();

        foreach (var docType in ExpectedDocTypes)
        {
            if (!latestByDocType.TryGetValue(docType, out var doc))
            {
                issues.Add(MissingDocumentIssue(docType));
                continue;
            }

            // Low-confidence classification Minor per spec — only when
            // classification landed in the mid-band. < 0.50 was stored as
            // DocType.Other and won't reach this loop (filtered above).
            if (doc.DocTypeConfidence is { } conf
                && conf >= LowConfidenceFloor && conf < TrustConfidenceFloor)
            {
                issues.Add(LowConfidenceClassificationIssue(doc, conf));
            }

            if (!extractionsByDocId.TryGetValue(doc.Id, out var ext))
            {
                // Document exists but no extraction row ever landed. Treat as
                // an extraction failure — Path B always writes an extraction
                // row (Succeeded or Failed) for dispatchable doc types, so
                // missing means the upload aborted between Document commit
                // and extraction commit. Rare; still actionable.
                issues.Add(ExtractionFailedIssue(doc, "No extraction row found for this document."));
                continue;
            }

            if (ext.Status == ExtractionStatus.Failed)
            {
                issues.Add(ExtractionFailedIssue(doc, ext.Error ?? "Unknown extractor failure."));
                continue;
            }

            // Succeeded → deserialize + populate.
            try
            {
                var (fields, locs, confs) = LoadJsonbTriple(ext);

                switch (docType)
                {
                    case DocType.License:
                        license = ParseLicense(fields);
                        PopulateProvenance(provenance, "license", fields, locs, confs, doc.Id);
                        TryAddName(nameCandidates, DocType.License, fields);
                        break;
                    case DocType.Dea:
                        dea = ParseDea(fields);
                        PopulateProvenance(provenance, "dea", fields, locs, confs, doc.Id);
                        TryAddName(nameCandidates, DocType.Dea, fields);
                        break;
                    case DocType.BoardCert:
                        boardCert = ParseBoardCert(fields);
                        PopulateProvenance(provenance, "boardCert", fields, locs, confs, doc.Id);
                        TryAddName(nameCandidates, DocType.BoardCert, fields);
                        break;
                    case DocType.Malpractice:
                        // No MalpracticeInfo on ProviderProfile yet — store
                        // provenance so a future validator can read it without
                        // re-aggregating, but don't overlay onto the profile.
                        PopulateProvenance(provenance, "malpractice", fields, locs, confs, doc.Id);
                        TryAddName(nameCandidates, DocType.Malpractice, fields);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Aggregator failed to parse extraction: docType={DocType}, extractionId={ExtractionId}",
                    docType, ext.Id);
                issues.Add(ExtractionFailedIssue(doc, $"Aggregator parse failure: {ex.Message}"));
            }
        }

        // Cross-doc fullName reconciliation. License wins; other docs that
        // disagree by Levenshtein ≥ 3 surface as a Minor. < 3 is typo or
        // credential-suffix noise.
        var resolvedFullName = ResolveFullName(nameCandidates, issues, basics.FullName);

        var profile = basics with
        {
            FullName = resolvedFullName,
            License = license,
            Dea = dea,
            BoardCert = boardCert,
            // Sanctions stays as the basics value (PSV is P5).
        };

        return new AggregatedProfile(profile, provenance, issues);
    }

    // === Issue factories ================================================

    private static Issue MissingDocumentIssue(DocType docType) => new(
        Validator: "DocumentStore",
        Severity: Severity.Critical,
        Message: $"No {docType} document on file for this provider.",
        Remediation: $"Upload a {docType} PDF via POST /api/providers/{{id}}/documents.",
        Citations: Array.Empty<Citation>());

    private static Issue ExtractionFailedIssue(Document doc, string error) => new(
        Validator: "DocumentStore",
        Severity: Severity.Critical,
        Message: $"Latest extraction for {doc.DocType} document failed: {error}",
        Remediation: "Retry via POST /api/documents/{id}/reextract; if persistent, re-upload the PDF.",
        Citations: new[]
        {
            new Citation(
                SourceValidator: "DocumentStore",
                ExtractedValue: error,
                DocumentId: doc.Id,
                Page: 1,
                Bbox: null),
        });

    private static Issue LowConfidenceClassificationIssue(Document doc, double conf) => new(
        Validator: "DocumentStore",
        Severity: Severity.Minor,
        Message: $"Classifier reported low confidence ({conf:F2}) for this {doc.DocType} document.",
        Remediation: "Re-upload a higher-quality scan, or manually confirm the doc type.",
        Citations: new[]
        {
            new Citation(
                SourceValidator: "DocumentStore",
                ExtractedValue: $"docTypeConfidence={conf:F2}",
                DocumentId: doc.Id,
                Page: 1,
                Bbox: null),
        });

    // === JSONB → typed-record parsers ==================================

    private static (JsonElement Fields, JsonElement Locations, JsonElement Confidences) LoadJsonbTriple(
        DocumentExtraction ext)
    {
        // JsonDocument is IDisposable but we return JsonElements; the elements
        // are valid while the documents are alive. Park the documents in a
        // static buffer to keep them alive for the duration of the aggregation
        // — alternatively we could materialize into Dictionaries here. Going
        // with the latter for safety.
        using var fieldsDoc = JsonDocument.Parse(ext.FieldsJson);
        using var locsDoc = JsonDocument.Parse(ext.FieldLocationsJson);
        using var confDoc = JsonDocument.Parse(ext.ConfidenceJson);

        return (fieldsDoc.RootElement.Clone(), locsDoc.RootElement.Clone(), confDoc.RootElement.Clone());
    }

    private static LicenseInfo? ParseLicense(JsonElement fields)
    {
        var number = StringOrNull(fields, "licenseNumber");
        var state = StringOrNull(fields, "state");
        var issueDate = DateOnlyOrNull(fields, "issueDate");
        var expiryDate = DateOnlyOrNull(fields, "expiryDate");
        var status = ParseEnum<LicenseStatus>(StringOrNull(fields, "status"));

        if (number is null || state is null || issueDate is null || expiryDate is null)
            return null;
        return new LicenseInfo(number, state, issueDate.Value, expiryDate.Value, status);
    }

    private static DeaInfo? ParseDea(JsonElement fields)
    {
        var number = StringOrNull(fields, "deaNumber");
        var expiryDate = DateOnlyOrNull(fields, "expiryDate");
        var status = ParseEnum<DeaStatus>(StringOrNull(fields, "status"));

        var schedules = new List<DeaSchedule>();
        if (fields.TryGetProperty("schedules", out var schedEl)
            && schedEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schedEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && Enum.TryParse<DeaSchedule>(item.GetString(), ignoreCase: false, out var s))
                {
                    schedules.Add(s);
                }
            }
        }

        if (number is null || expiryDate is null)
            return null;
        return new DeaInfo(number, expiryDate.Value, status, schedules);
    }

    private static BoardCertInfo? ParseBoardCert(JsonElement fields)
    {
        var board = StringOrNull(fields, "board");
        var specialty = StringOrNull(fields, "specialty");
        var issueDate = DateOnlyOrNull(fields, "issueDate");
        var expiryDate = DateOnlyOrNull(fields, "expiryDate");
        var status = ParseEnum<BoardCertStatus>(StringOrNull(fields, "status"));

        if (board is null || specialty is null || issueDate is null || expiryDate is null)
            return null;
        return new BoardCertInfo(board, specialty, issueDate.Value, expiryDate.Value, status);
    }

    // === Provenance map ================================================

    private static void PopulateProvenance(
        Dictionary<string, FieldProvenance> map,
        string docTypePrefix,
        JsonElement fields,
        JsonElement locations,
        JsonElement confidences,
        Guid documentId)
    {
        foreach (var fieldProp in fields.EnumerateObject())
        {
            var key = $"{docTypePrefix}.{fieldProp.Name}";

            // Missing location → skip provenance for this field. The field
            // value still flows into the typed-record (above); validators
            // that cite it will produce a citation without doc-ref details.
            if (!locations.TryGetProperty(fieldProp.Name, out var locEl)
                || locEl.ValueKind != JsonValueKind.Object)
                continue;

            var page = locEl.TryGetProperty("page", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                ? pEl.GetInt32() : 1;

            var bbox = ParseBbox(locEl);

            // Per spec §"Why confidence as its own column": missing key
            // defaults to 0.0 — fail loud on uncertainty.
            var conf = 0.0;
            if (confidences.TryGetProperty(fieldProp.Name, out var cEl)
                && cEl.ValueKind == JsonValueKind.Number)
            {
                conf = cEl.GetDouble();
            }

            map[key] = new FieldProvenance(documentId, page, bbox, conf);
        }
    }

    private static BoundingBox ParseBbox(JsonElement locEl)
    {
        // Sonnet self-reports bbox as [x, y, w, h] (PDF points, top-left
        // origin). Domain.BoundingBox is X1Y1X2Y2; convert at this boundary.
        // Default to a unit-size bbox at origin so a malformed value doesn't
        // crash the aggregator — the dashboard will render a tiny highlight
        // rather than nothing.
        if (!locEl.TryGetProperty("bbox", out var bboxEl)
            || bboxEl.ValueKind != JsonValueKind.Array
            || bboxEl.GetArrayLength() < 4)
        {
            return new BoundingBox(0, 0, 1, 1);
        }

        var x = bboxEl[0].GetDouble();
        var y = bboxEl[1].GetDouble();
        var w = bboxEl[2].GetDouble();
        var h = bboxEl[3].GetDouble();
        return new BoundingBox(x, y, x + w, y + h);
    }

    // === FullName reconciliation =======================================

    private static void TryAddName(
        List<(DocType Source, string FullName)> bucket,
        DocType source,
        JsonElement fields)
    {
        var name = StringOrNull(fields, "fullName");
        if (!string.IsNullOrWhiteSpace(name))
            bucket.Add((source, name));
    }

    private static string ResolveFullName(
        List<(DocType Source, string FullName)> candidates,
        List<Issue> issues,
        string fallback)
    {
        if (candidates.Count == 0) return fallback;

        // Precedence: License > Dea > BoardCert > Malpractice. First match
        // wins. The dashboard renders ProviderProfile.FullName everywhere;
        // this is the canonical answer.
        var winner = candidates
            .OrderBy(c => Array.IndexOf(ExpectedDocTypes, c.Source))
            .First();

        // Compare normalized names: strip credential suffixes (", MD" / ", DO"
        // / ", MBBS"), trim, lowercase. DEA cards typically omit credentials
        // while license cards carry them — a raw Levenshtein would emit a
        // Minor on every clean packet for that gap. Real disagreements (typos,
        // wrong-person uploads) still surface against the normalized form.
        var winnerNormalized = NormalizeForNameCompare(winner.FullName);
        foreach (var other in candidates.Where(c => c.Source != winner.Source))
        {
            var distance = Levenshtein(winnerNormalized, NormalizeForNameCompare(other.FullName));
            if (distance >= FullNameLevenshteinFloor)
            {
                issues.Add(new Issue(
                    Validator: "DocumentStore",
                    Severity: Severity.Minor,
                    Message: $"fullName disagrees across docs: {winner.Source}='{winner.FullName}' vs {other.Source}='{other.FullName}' (distance {distance}).",
                    Remediation: "Confirm the provider's legal name; re-upload the off-document with corrected text if needed.",
                    Citations: Array.Empty<Citation>()));
            }
        }

        return winner.FullName;
    }

    // Strip the credential suffixes the dataset sees in practice (", MD",
    // ", DO", ", MBBS", ", PhD"), case-fold, collapse interior whitespace.
    // List is intentionally small + alphabet-pinned; broader title handling
    // (Nurse Practitioner, PA-C) waits until the dataset shows the need.
    internal static string NormalizeForNameCompare(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var s = name;
        foreach (var suffix in CredentialSuffixes)
        {
            // Case-insensitive contains-trim. Picks up ", MD" but also bare
            // " MD" at the end (rare; a stray space-separated suffix).
            var idx = s.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && idx >= s.Length - suffix.Length - 2)
            {
                s = s[..idx].TrimEnd(' ', ',').Trim();
            }
        }
        return s.ToLowerInvariant().Trim();
    }

    private static readonly string[] CredentialSuffixes =
    {
        ", MD", ", DO", ", MBBS", ", PhD", ", DNP", ", NP", ", PA",
    };

    // Iterative Levenshtein with O(min(m,n)) space. Good enough for the
    // ~30-char strings we compare here; benchmark before swapping for SIMD
    // libraries.
    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Ensure b is the shorter — keeps the row buffer minimal.
        if (b.Length > a.Length) (a, b) = (b, a);

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    // === JSON helpers ===================================================

    private static string? StringOrNull(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static DateOnly? DateOnlyOrNull(JsonElement obj, string propertyName)
    {
        var s = StringOrNull(obj, propertyName);
        if (s is null) return null;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    private static T ParseEnum<T>(string? value) where T : struct, Enum
    {
        if (value is null) return default;
        return Enum.TryParse<T>(value, ignoreCase: true, out var v) ? v : default;
    }
}
