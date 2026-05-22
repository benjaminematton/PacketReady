# Phase 3 — Extractors + Classifier

> PDFs in, structured fields out, real numbers in `baseline.json`. The P2 stub at `POST /api/extract` gets a body; everything downstream of it stays still.

| | |
|---|---|
| **Parent** | [build-plan.md](../build-plan.md) — Phase 3 row |
| **Goal** | Real extractions flow through the harness. Phase 1's score path reads from extractions, not JSON fixtures. |
| **Status** | Not started |
| **Data** | synthetic PDFs from P2 dataset · no PHI · operator-only |
| **Depends on** | [Phase 2](./phase-2-eval-harness.md) — closed 2026-05-22 |
| **Style** | [../style.md](../style.md) |

---

## Definition of done

- [ ] EF migrations land two tables — `documents` and `document_extractions` — with the append-only `BEFORE UPDATE → RAISE` trigger on `document_extractions`. Both tables visible in the latest model snapshot.
- [ ] `POST /api/extract` (the P2-locked surface) extracts the uploaded PDF in-memory for the caller-supplied `docType` and returns `{ fields }`. **Stateless**: no `documents` row, no `document_extractions` row, no idempotency cache, no classifier call (the eval runner already knows `docType` from `golden.json`). Same wire shape as P2 — the body went from empty to populated.
- [ ] `POST /api/providers/{id}/documents` accepts a multipart PDF, persists the bytes to the local blob store, writes a `documents` row, runs the Haiku classifier inline, runs the Sonnet extractor inline, persists a `document_extractions` row, and returns `{ documentId, docType, docTypeConfidence, extractionId }`. **Stateful**: this is the intake path, idempotent on `(document_id, schema_version, model, prompt_hash)`.
- [ ] Four prompt files checked in at `apps/api/Application/Extraction/Prompts/{License,Dea,BoardCert,Malpractice}ExtractionPrompt.v1.md` + one `ClassifierPrompt.v1.md`. Each row in `document_extractions` carries `schema_version = '<docType>.v1'` and a `prompt_hash` matching the file's SHA-256. Each row in `documents` carries `classifier_model` + `classifier_prompt_hash` for the same audit reason.
- [ ] Idempotency on the stateful path: `POST /api/documents/{id}/reextract` against an unchanged `(document_id, schema_version, model, prompt_hash)` tuple returns the existing `extraction_id` without re-billing Sonnet. (Re-uploading the same PDF via `POST /api/providers/{id}/documents` creates a new `documents` row by design — see decisions table on `document_id` provenance.) Visible in Langfuse — one trace per unique tuple, not per request.
- [ ] `POST /api/providers/{id}/scores` reads the latest extraction per `doc_type` for the provider, aggregates into a `ProviderProfile`, and feeds the existing Phase 1 scorer. The endpoint stops accepting a JSON body for the profile; the body is now `{ providerId }` only.
- [ ] `python -m runners.run evals/dataset/` against the live API produces a non-zero accuracy table. `evals/results/baseline.json` is rewritten with `stub: false` and real per-field numbers, in the same PR that flips `stub`. Eval runs re-bill Sonnet on every invocation (~$0.45/run, extract-only — no classifier on Path A) — accepted tradeoff for keeping `/api/extract` pure.
- [ ] Citation drill-in works in the dashboard: clicking an Issue's citation opens the source PDF at the right page; on scanned documents the bbox highlight degrades gracefully to page-only. Citation provenance reaches the score response via the aggregator's `FieldProvenance` map → validators carry it on emitted `Issue.Citation`.

All eight boxes check → Phase 3 closes. Move to [Phase 4 — Scale to 50 + LLM validators](./phase-4-scale-and-validators.md).

---

## Stack additions

| Layer | Addition | Why |
|---|---|---|
| Backend | `Microsoft.Extensions.AI.Abstractions` (already in `Application.csproj`) | Structured-output `IChatClient` calls without a custom JSON pipeline. |
| Backend | `Anthropic.SDK` (already wired in P0) | One client; Haiku and Sonnet differ by `model` parameter, not by SDK. |
| Backend | Local blob store — folder under `apps/api/Api/blob-store/` (gitignored) | LocalStack/S3 abstraction is P6 work; a folder + UUID filename is enough through the demo. |
| Backend | EF migration — `documents` + `document_extractions` + append-only trigger | The persistence contract from design.md §7.1, no shortcuts. |
| Dashboard | PDF viewer — `react-pdf` | Citation drill-in. Picked over `pdf.js` direct integration: same renderer, less ceremony. |

**No new Python deps.** The eval runner is unchanged. It calls the same `POST /api/extract` it called against the P2 stub; only the response payload changes.

---

## Decisions baked in (taken here, locked through P4)

| Decision | Choice | Reasoning |
|---|---|---|
| Classifier model | Claude Haiku 4.5 | Single-label task; ~6x cheaper than Sonnet, ~2x faster. Mirrors VaBene's inquiry-classifier split. |
| Extractor model | Claude Sonnet 4.6 | Long-tail edge cases (multi-line addresses, ambiguous dates, scanned-doc OCR artifacts) need reasoning. Haiku flunked early bench on packet-005-scanned. |
| Extractor count for P3 | 4 (license, dea, boardCert, malpractice) | Matches the P2 dataset exactly. CV waits for P4 — no point shipping a fifth extractor with no packet exercising it. |
| `/api/extract` responsibility | **Stateless** — extract in-memory for the caller-supplied `docType`, return `{ fields }`, no DB writes, no classifier call. Used by the eval runner only. | The eval runner has PDFs on disk, no `providerId`, no upload step, and already knows `docType` from `golden.json`. Conflating it with persistence would require a fake-provider FK hack or pollute the `documents` table with eval rows. Cost: Sonnet re-bills on every eval run (~$0.45/run, extractor-only), survivable through the demo. Content-hash cache is a later add. |
| `/api/providers/{id}/documents` responsibility | **Stateful** — uploads blob, persists `documents` row, runs classifier + extractor inline, persists `document_extractions` row, idempotent on the content-addressed key. The intake path. | Two responsibilities, two endpoints. Application code shares the same `IClassifier` + `IExtractor` services; the difference is whether the result lands in the DB. |
| Blob storage | Local filesystem | Single-process API. S3 contract documented for P6, but a folder works through the demo. Migrating the field is `IBlobStore.PutAsync` swap — three callers, two days. |
| Idempotency key | `(document_id, schema_version, model, prompt_hash)` | Model is in the key so a Sonnet 4.6 → 4.7 bump invalidates the cache automatically. If model were only an audit column, an extractor swap would silently return stale extractions. Schema version describes the prompt + JSON schema, not the model. |
| Classifier audit fields | `documents.classifier_model` + `documents.classifier_prompt_hash` columns | Same provenance discipline extractor rows already have. Two columns; without them, a `ClassifierPrompt.v1.md` edit silently re-attributes every prior classification. |
| Bbox citation | Sonnet self-report on native PDFs; page-only fallback on scanned docs | Sonnet's bbox accuracy degrades on rasterized inputs (build-plan risk #2). Detection: PDF has no extractable text layer = scanned, fall back. |
| Citation provenance channel | Aggregator returns `ProviderProfile` + parallel `Dictionary<string, FieldProvenance>` keyed by `<docType>.<field>`. Validators read both, emit `Issue.Citation` populated with `{ documentId, page, bbox }`. | Option A from review — least invasive. Keeps `ProviderProfile` a value type (validators stay pure-code, testable with Moq per project_test_infrastructure_reality). Option B (`CitedField<T>` wrappers) was rejected as invasive; option C (validators query the DB) was rejected because it makes validators DB-aware. |
| Aggregator policy on conflicts | Keep per-doc fields; derive `ProviderProfile.FullName` by license-precedence; flag a Minor Issue if any other doc disagrees by Levenshtein ≥ 3 | From phase-2.md §"fullName per doc"; option (a). Cheapest. Leaves P4 validators room to flip a Minor → Critical with cross-doc evidence. |
| Re-extraction trigger | Manual `POST /api/documents/{id}/reextract` only | No background polling, no auto-retry. P3 is happy-path; failures escalate via the Issue list, not a re-queue. |
| Prompt versioning | Embedded `.md` resources, suffix-matched via existing `PromptLoader` | Already shipped in P0. Hashing the embedded resource bytes is one SHA-256 call at load. |
| Extraction failure surface | Persist a row with `fields = {}`, `status = 'Failed'`, `error = '<reason>'`; aggregator skips it; scorer sees a missing-doc Issue | Failure is data, not exception. A broken extraction is information the score should reflect, not a 500. |

The two open lanes from build-plan that **don't** lock here: object storage (P6) and the full 5-extractor set (P4 with CV). Resist scope drift on either.

---

## Project layout deltas

```
PacketReady/
├── apps/
│   └── api/
│       ├── Api/
│       │   ├── blob-store/                              NEW (gitignored)
│       │   └── Endpoints/
│       │       ├── ExtractEndpoint.cs                    REWIRED (body replaced; surface unchanged)
│       │       ├── DocumentEndpoints.cs                  NEW (upload + reextract)
│       │       └── ScoreEndpoint.cs                      MODIFIED (body shrinks to { providerId })
│       ├── Application/
│       │   ├── Extraction/                               NEW
│       │   │   ├── Classify/
│       │   │   │   ├── ClassifyDocumentCommand.cs
│       │   │   │   └── ClassifyDocumentCommandHandler.cs
│       │   │   ├── Extract/
│       │   │   │   ├── ExtractDocumentCommand.cs
│       │   │   │   ├── ExtractDocumentCommandHandler.cs
│       │   │   │   └── DocTypeExtractors/
│       │   │   │       ├── ILicenseExtractor.cs
│       │   │   │       ├── IDeaExtractor.cs
│       │   │   │       ├── IBoardCertExtractor.cs
│       │   │   │       └── IMalpracticeExtractor.cs
│       │   │   ├── Prompts/                              NEW (embedded resources)
│       │   │   │   ├── ClassifierPrompt.v1.md
│       │   │   │   ├── LicenseExtractionPrompt.v1.md
│       │   │   │   ├── DeaExtractionPrompt.v1.md
│       │   │   │   ├── BoardCertExtractionPrompt.v1.md
│       │   │   │   └── MalpracticeExtractionPrompt.v1.md
│       │   │   └── PromptHasher.cs                       SHA-256 over the embedded resource bytes
│       │   └── Providers/
│       │       └── ProviderProfileAggregator.cs          NEW (latest-extraction → ProviderProfile)
│       ├── Domain/
│       │   └── Documents/                                NEW
│       │       ├── Document.cs
│       │       ├── DocumentExtraction.cs
│       │       ├── DocType.cs                            enum: License | Dea | BoardCert | Malpractice | Cv | Other
│       │       └── ExtractionStatus.cs                   enum: Succeeded | Failed
│       └── Infrastructure/
│           ├── Blob/
│           │   ├── IBlobStore.cs                          NEW
│           │   └── LocalFileBlobStore.cs                  NEW
│           ├── Extraction/                                NEW
│           │   ├── HaikuDocumentClassifier.cs
│           │   └── SonnetExtractors/
│           │       ├── LicenseExtractor.cs
│           │       ├── DeaExtractor.cs
│           │       ├── BoardCertExtractor.cs
│           │       └── MalpracticeExtractor.cs
│           └── Persistence/
│               ├── Configurations/
│               │   ├── DocumentConfiguration.cs           NEW
│               │   └── DocumentExtractionConfiguration.cs NEW
│               └── Migrations/<timestamp>_AddDocumentStore.cs   NEW
└── evals/
    └── results/
        ├── baseline.json                                  REWRITTEN (stub:false + real numbers)
        └── latest.json                                    (regenerated)
```

The P2 stub at `ExtractEndpoint.cs` keeps its file path, its route, and its response shape. Only the lambda body changes — the locked contract held.

---

## Document store schema

```sql
-- 0002_add_document_store.sql (logical; EF emits the C# migration).
-- Lives in Infrastructure/Persistence/Migrations/. Output dir is explicit per
-- project_migration_folder_consolidation (two migration folders is a footgun).

CREATE TABLE documents (
  id                       UUID PRIMARY KEY,
  provider_id              UUID NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
  doc_type                 TEXT,                  -- License | Dea | BoardCert | Malpractice | Cv | Other
  doc_type_conf            FLOAT,                 -- 0.00–1.00, Haiku self-report
  classifier_model         TEXT NOT NULL,         -- 'claude-haiku-4-5'
  classifier_prompt_hash   TEXT NOT NULL,         -- SHA-256 of ClassifierPrompt.v1.md
  storage_uri              TEXT NOT NULL,         -- file:///… or s3://… (P3 emits file://)
  original_name            TEXT NOT NULL,
  mime_type                TEXT NOT NULL,
  page_count               INT NOT NULL,
  uploaded_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  uploaded_by              TEXT NOT NULL          -- 'provider' | 'admin'
);

CREATE INDEX ix_documents_provider_doctype ON documents (provider_id, doc_type);

CREATE TABLE document_extractions (
  id              UUID PRIMARY KEY,
  document_id     UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  extraction_id   INT NOT NULL,                 -- monotonic per document_id (1, 2, …); see "Why per-document extraction_id" below
  schema_version  TEXT NOT NULL,                -- 'license.v1', 'dea.v1', …
  status          TEXT NOT NULL,                -- 'Succeeded' | 'Failed'
  fields          JSONB NOT NULL,               -- camelCase keys; {} on Failed
  field_locations JSONB NOT NULL,               -- { field: { page, bbox: [x,y,w,h] } }; {} on Failed
  confidence      JSONB NOT NULL,               -- { field: 0.00–1.00 }; {} on Failed
  error           TEXT,                          -- NULL when status='Succeeded'
  model           TEXT NOT NULL,                -- 'claude-sonnet-4-6'; part of idempotency key
  prompt_hash     TEXT NOT NULL,                -- SHA-256 of the embedded extractor prompt resource
  input_tokens    INT NOT NULL,
  output_tokens   INT NOT NULL,
  extracted_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  UNIQUE (document_id, extraction_id),
  UNIQUE (document_id, schema_version, model, prompt_hash)   -- idempotency includes model
);

-- Append-only: BEFORE UPDATE raises. Phase 1's audit_events table uses the same
-- pattern; the trigger function is reusable.
CREATE OR REPLACE FUNCTION raise_immutable() RETURNS TRIGGER AS $$
BEGIN
  RAISE EXCEPTION 'document_extractions is append-only (row %)', OLD.id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER document_extractions_immutable
  BEFORE UPDATE ON document_extractions
  FOR EACH ROW EXECUTE FUNCTION raise_immutable();
```

**Why the unique-by-(model, prompt_hash) constraint:** it does the idempotency work the application would otherwise have to remember to do. A duplicate insert raises; the handler catches it and returns the existing `extraction_id`. No race, no double-billing. `model` is in the key so a Sonnet version bump invalidates the cache automatically — otherwise the cache would silently return stale extractions and the audit column would be the only signal anything changed.

**Why `confidence` as its own column:** P4's validators read it directly to gate Critical-eligible checks at ≥ 0.85. Pulling it out of `fields` avoids re-parsing the JSON every validation run.

**Why classifier audit on `documents`:** the classifier's Haiku call is the upstream provenance for every extraction. If `ClassifierPrompt.v1.md` changes and no audit column captures it, every prior `doc_type` is silently re-attributed to the new prompt. Two columns close the loop.

**Why per-document `extraction_id` (and how to allocate it safely):** the human-meaningful identifier is "extraction #2 of document X," not a global UUID. To allocate without a race: take `pg_advisory_xact_lock(hashtext(document_id::text))` at the start of the insert transaction, then `INSERT … SELECT COALESCE(MAX(extraction_id), 0) + 1 FROM document_extractions WHERE document_id = $1`. The advisory lock serializes concurrent inserts against the same document; the `UNIQUE (document_id, extraction_id)` constraint is the belt-and-braces — if two transactions ever skip the lock, the second insert raises and the handler retries with `MAX + 1` again.

**Migration audit before `dotnet ef migrations add`:** confirm `ModelSnapshot.cs` is clean before adding (per project_migration_snapshot_drift). If P1's snapshot has drifted, fix the drift in a separate commit before this migration lands.

---

## Extraction flow — two paths

> Diagram notation: `…Cmd` is the abbreviated form of the C# `…Command` class (`ExtractInMemoryCommand`, `UploadDocumentCommand`) — shortened to fit the column width.

### Path A — stateless (`/api/extract`, eval runner)

```
eval runner             Api                  Application                Infrastructure
   │                     │                        │                            │
   │ POST /api/extract   │                        │                            │
   │ (file, docType) ───►│                        │                            │
   │                     │ ExtractInMemoryCmd ───►│                            │
   │                     │                        │ SonnetExtractor.Extract ──►│ Anthropic API
   │                     │                        │       (4.6 + JSON schema)  │
   │                     │                        │◄── { fields, locs, conf } ─│
   │                     │◄── { fields (flat) } ──│                            │
   │ 200 { fields } ◄────│                        │                            │
```

No DB writes. No classifier call (`docType` is in the request body). No idempotency. Sonnet bills on every call. Eval runs at ~$0.45 per 20-doc pass (5 packets × 4 doc types × ~$0.022 avg) — accepted; a content-hash cache layers in cleanly later if it bites.

### Path B — stateful (`/api/providers/{id}/documents`, intake)

```
intake client          Api                   Application                Infrastructure
   │                    │                          │                            │
   │ POST /providers/   │                          │                            │
   │   {id}/documents   │                          │                            │
   │ (multipart PDF) ──►│                          │                            │
   │                    │ UploadDocumentCmd ──────►│                            │
   │                    │                          │ IBlobStore.PutAsync ──────►│ local FS
   │                    │                          │ HaikuClassifier ──────────►│ Anthropic API
   │                    │                          │ persist `documents` row    │
   │                    │                          │   (incl. classifier_model, │
   │                    │                          │    classifier_prompt_hash) │
   │                    │                          │                            │
   │                    │                          │ resolve idempotency key:   │
   │                    │                          │  (doc_id, schema_ver,      │
   │                    │                          │   model, prompt_hash)      │
   │                    │                          │  hit?  ─► return cached    │
   │                    │                          │  miss? ─► SonnetExtractor  │
   │                    │                          │           persist          │
   │                    │                          │           `document_       │
   │                    │                          │            extractions`    │
   │                    │◄── { documentId, docType,│                            │
   │                    │     confidence,          │                            │
   │                    │     extractionId } ──────│                            │
   │ 201 { … } ◄────────│                          │                            │
```

The two paths share `IClassifier` and `IExtractor` services in `Application/Extraction/`. The difference is whether the caller asks for persistence; the LLM-call logic is identical. The eval runner stays on Path A through the demo — wiring the harness to Path B would require an "eval pseudo-provider" or a nullable FK, both worse than re-billing Sonnet.

---

## Prompt strategy

Four extractor prompts + one classifier prompt, all embedded `.md` resources in the `Application` assembly. Each lives at `apps/api/Application/Extraction/Prompts/<DocType>ExtractionPrompt.v1.md`. Version bumps create a new file (`.v2.md`) and never edit `v1.md` — the prompt hash on existing extraction rows would otherwise lie about what produced them.

The `*Prompt.v*.md` suffix is deliberate: it matches the existing `PromptResourceValidator` (P0) glob without a validator change.

**Classifier output shape (Haiku, structured):**

```json
{
  "docType": "license",                  // one of: license | dea | boardCert | malpractice | cv | other
  "confidence": 0.94,                    // 0.0–1.0
  "rationale": "Title 'Physician License' and license-number field present"
}
```

The `rationale` is for Langfuse debugging only — not persisted to `documents`. If the field name shifts in P4, no schema migration.

**Extractor output shape (Sonnet, structured), license example:**

```json
{
  "fields": {
    "fullName":      { "value": "Henry Anderson, MD", "page": 1, "bbox": [120, 240, 380, 22] },
    "licenseNumber": { "value": "MD-NY-99001",        "page": 1, "bbox": [120, 280, 200, 22] },
    "state":         { "value": "NY",                  "page": 1, "bbox": [120, 320, 60,  22] },
    "issueDate":     { "value": "2020-04-15",          "page": 1, "bbox": [120, 360, 140, 22] },
    "expiryDate":    { "value": "2027-04-14",          "page": 1, "bbox": [120, 400, 140, 22] },
    "status":        { "value": "Active",              "page": 1, "bbox": [120, 440, 100, 22] }
  },
  "confidence": {
    "fullName": 0.97, "licenseNumber": 0.98, "state": 0.99,
    "issueDate": 0.95, "expiryDate": 0.95, "status": 0.93
  }
}
```

**Wire-shape flattening (Path A):** the eval runner expects a flat `{ "fields": { "fullName": "Henry Anderson, MD", … } }` per the P2 contract. The `/api/extract` endpoint flattens `fields[k].value → fields[k]` before returning; `field_locations` and `confidence` are discarded on Path A (no DB write, no consumer in the runner). This keeps the P2-locked surface intact and the eval harness untouched.

**Persistence (Path B):** the intake endpoint persists the full structured output — `fields` (flattened to value-only for the JSONB column), `field_locations`, and `confidence` — to `document_extractions`. The response body is `{ documentId, docType, docTypeConfidence, extractionId }`; no fields in the response (the dashboard reads them back through the score endpoint).

**Prompt-file shape (locked):**

```md
# License Extractor — v1

You are a credentialing analyst extracting structured fields from a state medical license PDF.

## Output rules
1. Dates are ISO YYYY-MM-DD. If only a year is visible, return null.
2. License number is verbatim, including any state-prefix hyphenation.
3. Status is one of: Active | Suspended | Expired | Probation | Unknown.
4. fullName preserves credential suffixes (MD, DO, MBBS) verbatim if printed.

## Bbox rules
Coordinates are (x, y, w, h) in PDF points (1/72 inch), origin top-left.
On scanned documents you cannot localize precisely; set bbox to the full page.

## Confidence
Per-field self-report on a 0.00–1.00 scale.
A field that you can read but can't normalize (e.g. "2024 (illegible)") is 0.50 or less.
```

Same skeleton for DEA / boardCert / malpractice, varying the field list and the doc-specific rules. Resist tone variation across prompts — uniform tone is uniform behavior.

---

## Idempotency policy (Path B only)

Path A (`/api/extract`) has no idempotency — it never writes. The block below is the intake path.

The unique-index does the heavy lifting; the application code is small:

```csharp
// UploadDocumentCommandHandler — pseudocode (Sonnet portion only).
var key = new ExtractionKey(documentId, schemaVersion, model, promptHash);

// 1) Look up by the same tuple. Same content addresses = same answer.
var existing = await _db.DocumentExtractions
    .AsNoTracking()
    .FirstOrDefaultAsync(e =>
        e.DocumentId == key.DocumentId &&
        e.SchemaVersion == key.SchemaVersion &&
        e.Model == key.Model &&
        e.PromptHash == key.PromptHash, ct);
if (existing is not null) return existing.ToDto();

// 2) Miss — call Sonnet, persist, return. On insert race, the unique index
//    catches it and we re-read instead of writing a duplicate.
var fresh = await _extractor.ExtractAsync(document, ct);
try
{
    _db.DocumentExtractions.Add(fresh);
    await _db.SaveChangesAsync(ct);
    return fresh.ToDto();
}
catch (DbUpdateException ex) when (ex.IsUniqueViolation())
{
    return (await _db.DocumentExtractions.FirstAsync(/* same predicate */, ct)).ToDto();
}
```

**What this does NOT do:** invalidate on PDF-bytes change. The idempotency key is on `(document_id, schema_version, model, prompt_hash)`, not on the blob hash. The implicit assumption is that documents are immutable post-upload — a re-upload creates a new `documents` row, not a re-extraction of the old one. If a customer wants to "replace" a doc, that's a P5 intake-state-machine concern, out of scope here.

**Prompt-hash discipline:** `PromptHasher.HashOf("LicenseExtractionPrompt.v1.md")` reads the embedded resource bytes and SHA-256s them. Editing `v1.md` after extractions exist invalidates the cache silently — every extraction row claims to be produced by a prompt that no longer exists at that hash. Don't do it. Promote to `v2.md` instead.

---

## Wire-up to the Phase 1 score path

`/api/providers/{id}/scores` currently accepts a `ProviderProfile` in the request body — that's the P1 hand-curated path. P3 inverts it: the body is `{ providerId }` only; the profile is derived from extractions.

```csharp
// ProviderProfileAggregator — single source of truth for extractions → ProviderProfile.

public sealed record AggregatedProfile(
    ProviderProfile Profile,
    IReadOnlyDictionary<string, FieldProvenance> Provenance,
    IReadOnlyList<AggregationIssue> Issues);

public sealed record FieldProvenance(
    Guid DocumentId,
    int Page,
    double[] Bbox,
    double Confidence);

public interface IProviderProfileAggregator
{
    Task<AggregatedProfile> AggregateAsync(Guid providerId, CancellationToken ct);
}
```

For each `doc_type` in (License, Dea, BoardCert, Malpractice):

1. Pull the latest `document_extractions` row for that document type for this provider (joined through `documents`).
2. If no document of that type exists, emit a Missing-Document Issue (Critical) — no `Citation` (there's nothing to point at).
3. If the latest extraction is `Failed`, emit an Extraction-Failed Issue (Critical) carrying `documents.id` + the persisted `error` so the dashboard shows "license PDF unreadable: timed out at page 1" with a link to the source.
4. If `Succeeded`, map `fields` JSONB → strongly-typed `LicenseInfo` / `DeaInfo` / etc.; populate `Provenance["license.fullName"] = new FieldProvenance(docId, page, bbox, conf)` for every scored field.

The aggregator also runs the cross-doc reconciliation policy (license precedence on `fullName`; Minor on Levenshtein ≥ 3). Per the locked decision, that's the cheapest place to put it — Phase 4's LLM validators will eventually upgrade some of those Minors to Criticals with their own cross-doc evidence.

### Citation provenance channel

The `Provenance` map travels alongside `ProviderProfile` into every validator. Validators look up `<docType>.<fieldName>` and attach a `Citation` to each emitted `Issue`:

```csharp
public Issue Validate(ProviderProfile p, IReadOnlyDictionary<string, FieldProvenance> prov)
{
    if (p.License.ExpiryDate < _clock.GetUtcNow().AddDays(30))
    {
        var src = prov["license.expiryDate"];
        return new Issue(
            severity: Severity.Critical,
            message: $"License expires {p.License.ExpiryDate:yyyy-MM-dd}.",
            citation: new Citation(src.DocumentId, src.Page, src.Bbox),
            confidence: src.Confidence);
    }
    return Issue.None;
}
```

Validators stay pure-code (no DB queries, no `IAppDbContext` injection) so they remain testable with Moq + `BuildMockDbSet` per project_test_infrastructure_reality. `Provenance` enters as a method parameter; no service-locator surface.

`AggregationIssue` flows into `ScoreSynthesizer`'s existing Issue list. The synthesizer is unchanged — it carries `Citation` through unmodified. The dashboard reads `Issue.Citation.DocumentId` to load the PDF and `{ Page, Bbox }` to anchor the highlight.

**Wire shape, score endpoint:**

```
POST /api/providers/{id}/scores
{ "providerId": "..." }   ← shrinks. No ProviderProfile in the body.

200 OK
{ ReadinessScore }        ← unchanged shape; Issues include aggregator + validator outputs.
```

Old callers passing a body still work for one PR — the body is parsed, ignored, and a warning logged. The fixture seed CLI moves to the new shape in the same PR that lands the aggregator. Lifecycle: warning for 1 PR, then 400 BadRequest, then the parameter is removed. Three PRs total.

---

## Cost / token budget

Per the build-plan §Phase 3 risk: **$0.40 / 4-PDF packet is the red line.** Below is the bench target; if real numbers blow past it, the mitigation is per-prompt token-count tightening (no model swap — Haiku-for-extraction was already evaluated and lost).

| Call | Model | Input tokens (target) | Output tokens (target) | Per-call cost (target) |
|---|---|---|---|---|
| Classify | Haiku 4.5 | ~3,500 (1-page vision) | ~80 | ~$0.005 |
| Extract — license | Sonnet 4.6 | ~5,000 (vision) | ~400 | ~$0.022 |
| Extract — dea | Sonnet 4.6 | ~4,500 | ~350 | ~$0.020 |
| Extract — boardCert | Sonnet 4.6 | ~4,500 | ~400 | ~$0.022 |
| Extract — malpractice | Sonnet 4.6 | ~5,500 (often 2 pages) | ~450 | ~$0.025 |
| **Per packet — Path B (intake)** | | | | **~$0.11** (4 doc-types × [1 classify + 1 extract]) |
| **Per packet — Path A (eval)** | | | | **~$0.09** (4 extracts only; no classifier — `docType` is supplied) |

Per-run eval cost: 5 packets × ~$0.09 = ~$0.45 (matches the DoD bullet 7 budget).

Caching is the second lever — Sonnet's prompt cache holds the system instructions across a packet's four extractions. If first-call costs trend higher than the table, set up explicit cache breakpoints on the system prompt's last line (Anthropic SDK `CacheControl = "ephemeral"`).

Langfuse is the source of truth; if a single packet exceeds $0.20, file a regression issue before flipping `stub: false`.

---

## Task order

1. **Migration scaffold.** `dotnet ef migrations add AddDocumentStore --output-dir Infrastructure/Persistence/Migrations`. Confirm `ModelSnapshot.cs` is clean *first*. Hand-edit the migration to include the `BEFORE UPDATE` trigger raw-SQL block (EF doesn't emit triggers).
2. **Domain entities + EF configuration.** `Document` (incl. `classifier_model` + `classifier_prompt_hash`), `DocumentExtraction`, `DocType`, `ExtractionStatus`. JSONB columns mapped via `HasColumnType("jsonb")` + `JsonDocument`.
3. **Local blob store.** `IBlobStore.PutAsync(stream, mime) → storageUri`. `LocalFileBlobStore` writes to `apps/api/Api/blob-store/<yyyy>/<MM>/<guid>.<ext>`. Gitignored.
4. **Classifier prompt + service.** `ClassifierPrompt.v1.md`, `HaikuDocumentClassifier`. Bench against the 5 P2 packets manually: every PDF classifies correctly with confidence ≥ 0.85 or P3 doesn't ship.
5. **Extractor prompts.** Four `*ExtractionPrompt.v1.md` files. Stub `IClassifier` + `IExtractor` handlers so the project compiles before LLM code exists.
6. **One extractor end-to-end against `/api/extract` (Path A).** Pick license — simplest layout, most variety. Implement `LicenseExtractor` (structured output + bbox capture). Wire it through the existing P2 stub endpoint; the body changes, the surface doesn't. Eval runner can now hit it directly; numbers go from all-zero to non-zero on license fields.
7. **Remaining three extractors.** DEA, board cert, malpractice. Same shape; each one is a couple hours. Eval runner now scores non-zero across all four doc types.
8. **Run the eval against Path A.** `python -m runners.run evals/dataset/ --check-against evals/results/baseline.json`. Confirm numbers are non-zero. If they're worse than I'd want, *don't* re-tune yet — commit the real `baseline.json` with `stub: false` first, then iterate against the gate.
9. **Path B intake endpoint.** `POST /api/providers/{id}/documents` — blob put, `documents` row insert (with classifier_model + classifier_prompt_hash), classifier call, extractor call with idempotency. 4xx if provider missing; 201 with `{ documentId, docType, docTypeConfidence, extractionId }` on success.
10. **Idempotency cache.** Pre-call lookup keyed on `(document_id, schema_version, model, prompt_hash)` + post-call unique-violation catch. Confirm two back-to-back identical Path B calls produce one Langfuse trace, not two.
11. **Aggregator + score endpoint rewire.** `ProviderProfileAggregator`. Update `ScoreEndpoint`. Old-body backwards-compat warning lands here; removal in PR+2. Wiring-only against the type system; live exercise requires `document_extractions` rows, which step 9 is the only producer of — run one Path B upload per packet first.
12. **Citation drill-in.** Dashboard reads `field_locations` from the extraction row; renders the source PDF at the right page; bbox overlay on native PDFs, page-only on scanned docs.
13. **Gate walk.** Eight DoD checkboxes.

Order matters: 2 depends on 1; 4 depends on 3; 6 depends on 4+5; 7 depends on 6; 8 depends on 7; 9 depends on 7 (extractors must exist before intake calls them); 10 depends on 9; 11 depends on 7 for code, on 9 for live data; 12 depends on 9 for live data (the citation drill-in reads `field_locations` from rows only Path B writes); 13 (gate walk) depends on a Path B sweep over the 5 P2 packets to populate the rows 11 and 12 read. The early eval-runner-first sequence (steps 6–8) is deliberate — it gets `baseline.json` flipped to `stub: false` and the gate load-bearing before the more involved Path B + aggregator work lands.

---

## Risks / open

- **Vision-cost overrun.** Sonnet vision is the most expensive call in the system. If the bench in §"Cost / token budget" diverges from real Langfuse numbers, mitigation is prompt-cache breakpoints + tighter system prompts. Model swap is not on the table — Haiku-for-extraction was evaluated and lost in early bench.
- **Bbox accuracy on scanned docs.** Sonnet self-reports bboxes. On the rasterized packet (005) those coordinates may drift; the page-only fallback exists for exactly this. Detection logic: PDF has no text layer = scanned; if uncertain, default to page-only and surface a Minor "low-fidelity citation" Issue.
- **Idempotency cache poisoning by silent prompt edit.** Editing `v1.md` after extractions exist invalidates the hash semantics — old rows claim a prompt hash that no longer matches any prompt on disk. Mitigation: `PromptResourceValidator` (P0) gets a check that every embedded `Extraction/Prompts/**/v*.md` resource has an unchanged hash since the most recent extraction referencing it. Adds 200ms to startup; cheap insurance.
- **Aggregator policy lock-in.** The chosen policy (license precedence on `fullName`, Minor on Levenshtein ≥ 3) is plausible but unbenchmarked. P4 validators will likely surface Critical name conflicts the aggregator under-counted as Minor. That's intended — the aggregator is the floor, validators raise the ceiling.
- **P2 packet accuracy leaking into outreach.** The first non-zero numbers on the 5-packet set will be tempting to quote. Don't. Per phase-2.md, the 50-packet P4 set is the README's claim. The P2 numbers are a regression signal, full stop.
- **Migration-folder drift.** Per project_migration_folder_consolidation, the repo has two migration folders historically. Pass `--output-dir Infrastructure/Persistence/Migrations` explicitly when running `dotnet ef migrations add`. Audit `ModelSnapshot.cs` is in the same folder.
- **Backend test reality.** Per project_test_infrastructure_reality, there's no DB-bound integration test infra. Aggregator + idempotency tests use Moq + the `BuildMockDbSet` helper; the `EF + Postgres` path is exercised manually via the live eval run and the Phase 2 gate. Don't try to wire EF integration tests during P3.

---

## Out of scope (resist)

- **CV extractor.** No P2 packet exercises it; building it without a test target invites silent bit rot. Lands in P4 alongside the 50-packet set.
- **LLM validators** (`identity_coherence`, `npi_taxonomy_match`). Phase 4. The aggregator covers the floor; cross-doc reasoning waits for the validator suite.
- **Conflict recall / precision metrics.** The Python runner doesn't score `plantedConflicts` yet. Phase 4 adds it.
- **Object-storage abstraction (S3 / LocalStack).** A folder works through the demo. Migration is a `IBlobStore` swap; defer.
- **Async extraction / queues.** Inline call inside the endpoint, < 10s per doc. If we hit > 30s real-world, that's a P5 problem when the intake portal can show a spinner.
- **Re-extraction backfill.** Bumping `v1.md` → `v2.md` creates a new schema_version; the old `v1` rows stay. Some endpoint or job to re-extract every document at the new prompt is genuinely useful — also genuinely P4.
- **Dashboard upload UI.** The upload endpoint exists; calling it lives in P5 when the intake portal arrives. P3's demo path is `curl -F file=… /api/providers/{id}/documents` from a terminal.
- **Confidence-threshold gates.** Per design.md §7.2, "confidence ≥ 0.85 to count as a passing input to a Critical-eligible validator" is a P4 concern (the validators don't exist yet to gate). P3 captures confidence and stores it; no consumer reads it.

---

## What gets written when Phase 3 closes

Append a one-line outcome note to [build-plan.md](../build-plan.md) Status for Phase 3. Flip `evals/results/baseline.json` from `stub: true` to `stub: false` in the same PR that lands the live extractor. From that PR forward, the > 2 pp regression gate is load-bearing on every PR.

Then write `phase-4-scale-and-validators.md`. Topics: programmatic packet generator (NPPES sampling, 50-packet bucket math 15/15/15/5), `identity_coherence` + `npi_taxonomy_match` Sonnet validators, `malpractice_currency` + `payer_specific` YAML-driven validators, cross-document conflict recall/precision metrics, Spearman correlation against a 20-packet hand-labeled tier set, the confidence-threshold gate (≥ 0.85 to count as a Critical-eligible input), README accuracy publishing.

The first thing P4 does is run `python -m packetready_eval.packets evals/dataset-50/` to materialize the 50-packet set. P3's 5-packet set stays put as the smoke-test bucket. Two datasets, two purposes — don't mix the numbers.
