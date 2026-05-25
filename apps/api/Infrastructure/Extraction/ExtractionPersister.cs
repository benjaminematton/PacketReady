using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Application.Extraction.Persist;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;
using PacketReady.Infrastructure.Persistence;

namespace PacketReady.Infrastructure.Extraction;

/// <summary>
/// Postgres-backed <see cref="IExtractionPersister"/>. Lives in Infrastructure
/// because the four-step dance pulls in Npgsql-specific machinery
/// (<c>pg_advisory_xact_lock</c>, <c>PostgresException.SqlState</c>) the
/// Application layer doesn't carry.
/// </summary>
internal sealed class ExtractionPersister : IExtractionPersister
{
    // Postgres SQLSTATE for unique_violation. Catching by string code rather
    // than by .NET exception type because Npgsql wraps the constraint name +
    // detail into PostgresException.SqlState — that's the load-bearing signal.
    private const string UniqueViolationSqlState = "23505";

    // The composite UNIQUE that backs the (documentId, schemaVersion, model,
    // promptHash) idempotency contract. Set in DocumentExtractionConfiguration
    // via HasDatabaseName(...). Pinning by name here means a future unrelated
    // UNIQUE on this table doesn't get silently swallowed as "race lost".
    private const string IdempotencyConstraintName = "ux_document_extractions_idempotency";

    // Takes the concrete PacketReadyDbContext (not IAppDbContext) because
    // advisory locks + BeginTransactionAsync need the DatabaseFacade, which
    // IAppDbContext intentionally hides from Application-layer consumers.
    // The persister IS infrastructure-layer machinery; coupling is fine here.
    private readonly PacketReadyDbContext _db;
    private readonly PromptHasher _hasher;
    private readonly ILogger<ExtractionPersister> _logger;

    public ExtractionPersister(
        PacketReadyDbContext db,
        PromptHasher hasher,
        ILogger<ExtractionPersister> logger)
    {
        _db = db;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<ExtractionPersistResult> PersistAsync(
        Document document,
        ReadOnlyMemory<byte> pdfBytes,
        IDocTypeExtractor extractor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(extractor);

        // Defense against a future caller wiring the wrong keyed extractor to
        // a document. The dispatcher (upload/reextract handlers) already keys
        // off document.DocType; this catches the case where someone resolves
        // the extractor differently and lands the wrong fields_json under the
        // wrong schema_version.
        if (document.DocType != extractor.DocType)
            throw new ArgumentException(
                $"Extractor doc type {extractor.DocType} does not match document doc type {document.DocType}.",
                nameof(extractor));

        var promptHash = await _hasher.HashOfAsync(extractor.PromptResourceName, ct);
        var model = extractor.Model;
        var schemaVersion = extractor.SchemaVersion;

        // Step 1 — idempotency pre-check. AsNoTracking because we're reading
        // to decide, not to mutate; the entity is never re-saved through this
        // path. `model` and `promptHash` are always non-null on the LLM path,
        // so standard SQL equality already excludes any future manual-edit
        // rows (model=NULL) — NULL ≠ anything; that's the UNIQUE constraint's
        // distinct-NULL story, not this read's.
        var cached = await _db.DocumentExtractions
            .AsNoTracking()
            .FirstOrDefaultAsync(e =>
                e.DocumentId == document.Id &&
                e.SchemaVersion == schemaVersion &&
                e.Model == model &&
                e.PromptHash == promptHash, ct);
        if (cached is not null)
        {
            // Log the full idempotency tuple so a "why was this a cache hit?"
            // investigation can be answered from logs alone. PromptHash is the
            // sha256 hex (64 chars); first 12 is enough to disambiguate without
            // bloating log lines.
            _logger.LogInformation(
                "Extraction cache hit: documentId={DocumentId}, schema={Schema}, model={Model}, promptHash={PromptHashPrefix}, extractionId={ExtractionId}",
                document.Id, schemaVersion, model, PromptHashPrefix(promptHash), cached.ExtractionId);
            return new ExtractionPersistResult(cached.ExtractionId, WasCacheHit: true, cached.Status);
        }

        // Step 2 — run the extractor outside any transaction. Failure becomes
        // data: persist a Failed row with the error message so the aggregator
        // can surface "extraction failed; here's why" rather than treating the
        // document as missing.
        ExtractionResult? success = null;
        string? failureError = null;
        int inputTokens = 0;
        int outputTokens = 0;
        try
        {
            success = await extractor.ExtractAsync(pdfBytes, ct);
            inputTokens = success.InputTokens;
            outputTokens = success.OutputTokens;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failureError = ex.Message;
            _logger.LogWarning(ex,
                "Extractor threw — persisting Failed row: documentId={DocumentId}, schema={Schema}",
                document.Id, schemaVersion);
        }

        // Step 3 — advisory-locked insert. The lock serializes extraction_id
        // allocation per document; the (document_id, extraction_id) UNIQUE
        // constraint is belt-and-braces if a future caller bypasses the lock.
        try
        {
            return await InsertNewExtractionAsync(
                document, extractor, model, promptHash, success, failureError,
                inputTokens, outputTokens, ct);
        }
        catch (DbUpdateException ex) when (IsIdempotencyRaceLost(ex))
        {
            // Step 4 — race lost. Someone else's insert won the idempotency
            // UNIQUE; re-read and return their row as a cache hit. The cost
            // (one wasted LLM call) is the trade for not holding a transaction
            // open across the LLM round-trip.
            _logger.LogInformation(
                "Idempotency race lost: documentId={DocumentId}, schema={Schema}, model={Model}, promptHash={PromptHashPrefix} — re-reading winner",
                document.Id, schemaVersion, model, PromptHashPrefix(promptHash));
            var winner = await _db.DocumentExtractions
                .AsNoTracking()
                .FirstAsync(e =>
                    e.DocumentId == document.Id &&
                    e.SchemaVersion == schemaVersion &&
                    e.Model == model &&
                    e.PromptHash == promptHash, ct);
            return new ExtractionPersistResult(winner.ExtractionId, WasCacheHit: true, winner.Status);
        }
    }

    private async Task<ExtractionPersistResult> InsertNewExtractionAsync(
        Document document,
        IDocTypeExtractor extractor,
        string model,
        string promptHash,
        ExtractionResult? success,
        string? failureError,
        int inputTokens,
        int outputTokens,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Advisory lock — transaction-scoped, released on commit/rollback.
        // Two concurrent transactions on the same documentId block here until
        // the first commits.
        //
        // Lock key is the first 8 bytes of the document GUID read as an int64.
        // Deterministic, parameter-binds cleanly as bigint, and avoids the
        // hashtext(text) route — which depended on Npgsql binding strings to
        // text and Postgres resolving hashtext(text) correctly; both true in
        // practice but neither contractually guaranteed. Distinct documentIds
        // whose first 8 bytes collide will serialize on the same lock — a
        // perf footnote (~2^32 docs to expect a collision), not a correctness
        // break; the (document_id, extraction_id) UNIQUE is the identity
        // backstop.
        var lockKey = BinaryPrimitives.ReadInt64LittleEndian(document.Id.ToByteArray());
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            ct);

        var nextExtractionId = (await _db.DocumentExtractions
            .Where(e => e.DocumentId == document.Id)
            .MaxAsync(e => (int?)e.ExtractionId, ct) ?? 0) + 1;

        var row = success is not null
            ? DocumentExtraction.CreateLlmSucceeded(
                documentId: document.Id,
                extractionId: nextExtractionId,
                schemaVersion: extractor.SchemaVersion,
                fieldsJson: success.FieldsJson,
                fieldLocationsJson: success.FieldLocationsJson,
                confidenceJson: success.ConfidenceJson,
                model: model,
                promptHash: promptHash,
                inputTokens: success.InputTokens,
                outputTokens: success.OutputTokens)
            : DocumentExtraction.CreateLlmFailed(
                documentId: document.Id,
                extractionId: nextExtractionId,
                schemaVersion: extractor.SchemaVersion,
                error: failureError!,
                model: model,
                promptHash: promptHash,
                inputTokens: inputTokens > 0 ? inputTokens : null,
                outputTokens: outputTokens > 0 ? outputTokens : null);

        _db.DocumentExtractions.Add(row);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Persisted extraction: documentId={DocumentId}, schema={Schema}, model={Model}, promptHash={PromptHashPrefix}, extractionId={ExtractionId}, status={Status}",
            document.Id, extractor.SchemaVersion, model, PromptHashPrefix(promptHash), nextExtractionId, row.Status);

        return new ExtractionPersistResult(nextExtractionId, WasCacheHit: false, row.Status);
    }

    private static string PromptHashPrefix(string promptHash) =>
        promptHash.Length <= 12 ? promptHash : promptHash[..12];

    // Pinned to the idempotency UNIQUE by constraint name — a future UNIQUE
    // added to this table (e.g. a derived dedup column) will NOT be treated
    // as "race lost". That keeps the cache-hit re-read honest: it only fires
    // when the row we're about to insert is the one we'd read back.
    private static bool IsIdempotencyRaceLost(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner is not null)
        {
            if (inner is PostgresException pg
                && pg.SqlState == UniqueViolationSqlState
                && pg.ConstraintName == IdempotencyConstraintName)
                return true;
            inner = inner.InnerException;
        }
        return false;
    }
}
