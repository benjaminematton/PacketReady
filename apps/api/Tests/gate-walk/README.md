# gate-walk

Top-of-pyramid integration smokes that exercise a whole phase end-to-end against
a live system. Sibling to [`migration-smoke/`](../migration-smoke/README.md), but
operates one level higher: where migration-smoke pins DB invariants the EF model
can't reach, gate-walk pins API + LLM + eval-runner invariants the unit tests
can't reach (no shared DI graph; no real Anthropic round-trip).

## Why not xUnit?

Same reasoning as `migration-smoke/`. A gate walk needs a live API process, real
Postgres, a real Anthropic round-trip, and the Python eval runner. Wrapping all
of that in an xUnit harness produces more test infrastructure than the runtime
under test — and the runtime is what you actually want to verify. A shell script
is read-once, debuggable line-by-line, and survives changes to test frameworks.

## Phase 3 — `phase-3.sh`

Walks the eight DoD checkboxes from
[`docs/impl/phase-3-extractors.md`](../../../../docs/impl/phase-3-extractors.md):

| # | What |
|---|---|
| 1 | `documents` + `document_extractions` tables present + BEFORE-UPDATE trigger installed |
| 2 | `POST /api/extract` returns populated fields (stateless / Path A) |
| 3 | `POST /api/providers/{id}/documents` persists Document + DocumentExtraction (stateful / Path B) |
| 4 | Both rows carry `classifier_prompt_hash` / `prompt_hash` (64-char SHA-256) and `schema_version='license.v1'` |
| 5 | `POST /api/documents/{id}/reextract` returns the same extractionId with `wasCacheHit=true`; no new extraction row |
| 6 | `POST /api/providers/{id}/scores` returns a `ReadinessScoreDto` whose Issues carry `Citation.documentId` populated from the aggregator's provenance map |
| 7 | `python -m runners.run evals/dataset/` produces `stub: false` results and passes the `--check-against evals/results/baseline.json` regression gate |
| 8 | `GET /api/documents/{id}/blob` streams the source PDF (magic-byte + byte-size round-trip) |

### Prerequisites

```bash
docker compose up -d                              # Postgres on :5433
set -a && source .env && set +a                   # ANTHROPIC_API_KEY + DB_CONNECTION_STRING
```

Plus `psql`, `curl`, `jq`, `dotnet` 10 SDK on PATH, and the Python eval-runner
venv at `evals/generators/.venv/`.

### Running

```bash
bash apps/api/Tests/gate-walk/phase-3.sh
```

Each gate prints `[gate N] PASS` or `[gate N] FAIL: <reason>`. The script exits
non-zero on the first failure and dumps the last 40 lines of the API log to
stderr for context. A clean run ends with `[gate-walk] ALL 8 GATES PASS`.

### Cost

~$0.48 in Anthropic credits per full run:
- 1× Haiku 4.5 classify on the curl Path B upload (~$0.005)
- 1× Sonnet 4.6 license extract on the same upload (~$0.022)
- 1× reextract (cache hit; no LLM call)
- Full Python eval runner pass: 5 packets × 4 extractors × ~$0.022 each
  (~$0.45). Matches the slice-6 baseline spend.

### What the script does NOT cover

- The 410 Gone branch on `/blob` (blob-store/document drift) — needs a
  destructive mid-run filesystem mutation. Smoke it manually:
  `rm "$(... | jq -r .storageUri | sed 's|file://||')"` then re-fetch the blob.
- The Failed-extraction path. The current Anthropic API + our schema produce
  Succeeded extractions on the P2 packets every time; force-failing one would
  require a synthesized PDF the extractor can't parse.
- The race-loss path on the persister's 23505 catch. Needs two concurrent
  reextract calls; not a single-script verification.

These are non-blocking — each is covered by unit tests or by code review.
