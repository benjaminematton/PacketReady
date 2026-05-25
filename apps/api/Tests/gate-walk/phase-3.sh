#!/usr/bin/env bash
# Phase 3 — Gate Walk
#
# Exercises the 8 DoD checkboxes from docs/impl/phase-3-extractors.md against
# a live API + Postgres + Anthropic. Burns ~$0.11 in Sonnet+Haiku credits per
# run (one packet, one classify + one extract, plus a re-extract that should
# cache-hit and re-bill nothing).
#
# Run from repo root after:
#   docker compose up -d                              # Postgres on :5433
#   set -a && source .env && set +a                   # ANTHROPIC_API_KEY etc.
#
#   bash apps/api/Tests/gate-walk/phase-3.sh
#
# Each gate prints `[gate N] PASS` or `[gate N] FAIL: <reason>` and the script
# exits non-zero on the first failure. A successful run ends with
# `[gate-walk] ALL 8 GATES PASS`.

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

# ── Config ───────────────────────────────────────────────────────────────
PACKET_DIR="evals/dataset/packet-001-clean-anderson"
API_URL="${PACKETREADY_API_URL:-http://localhost:5000}"

# Derive a libpq URI from the .NET-style DB_CONNECTION_STRING. psql can't
# parse Host=...;Port=...;Username=...;Password=... directly — it expects
# either KV pairs ("host=... port=...") or a postgresql:// URI. The URI is
# simpler to assemble and prints redactable in logs.
_kv() {
  echo "$DB_CONNECTION_STRING" | tr ';' '\n' \
    | awk -F= -v key="$1" 'tolower($1)==tolower(key) { print $2 }'
}
PG_URI="postgresql://$(_kv Username):$(_kv Password)@$(_kv Host):$(_kv Port)/$(_kv Database)"

PSQL_ARGS=(-v ON_ERROR_STOP=1 -X -q -t)
psql_q() { psql "$PG_URI" "${PSQL_ARGS[@]}" "$@"; }

require() {
  local what="$1"; local where="$2"
  command -v "$where" >/dev/null 2>&1 \
    || { echo "[gate-walk] FAIL: $what (need '$where' on PATH)"; exit 1; }
}
require "psql client"  psql
require "curl"         curl
require "jq"           jq
require "dotnet"       dotnet

[[ -n "${ANTHROPIC_API_KEY:-}" ]] \
  || { echo "[gate-walk] FAIL: ANTHROPIC_API_KEY not set (source .env first)"; exit 1; }
[[ -n "${DB_CONNECTION_STRING:-}" ]] \
  || { echo "[gate-walk] FAIL: DB_CONNECTION_STRING not set"; exit 1; }

# ── API lifecycle ────────────────────────────────────────────────────────
LOG=$(mktemp -t packetready-gate-walk.XXXXXX.log)
API_PID=""
stop_api() {
  if [[ -n "$API_PID" ]] && kill -0 "$API_PID" 2>/dev/null; then
    kill "$API_PID" 2>/dev/null || true
    wait "$API_PID" 2>/dev/null || true
  fi
}
trap 'rc=$?; stop_api; if (( rc != 0 )); then echo "--- last 40 lines of API log ($LOG):"; tail -40 "$LOG" || true; fi; exit $rc' EXIT

start_api() {
  echo "[setup] starting API on $API_URL (log: $LOG)"
  dotnet run --project apps/api/Api/Api.csproj --no-launch-profile \
    >"$LOG" 2>&1 &
  API_PID=$!

  local waited=0
  until grep -q "Application started" "$LOG" 2>/dev/null || (( waited > 60 )); do
    sleep 1; waited=$((waited + 1))
  done
  grep -q "Application started" "$LOG" \
    || { echo "[gate-walk] FAIL: API did not start within 60s"; exit 1; }
}

# ── Gate 1: migrations + immutability trigger ────────────────────────────
echo
echo "[gate 1] EF migrations + document_extractions BEFORE-UPDATE trigger"
dotnet ef database update \
  --project apps/api/Infrastructure/Infrastructure.csproj \
  --startup-project apps/api/Api/Api.csproj \
  >/dev/null

psql_q -c "SELECT to_regclass('public.documents')::text;" \
  | grep -qw documents \
  || { echo "[gate 1] FAIL: documents table missing"; exit 1; }
psql_q -c "SELECT to_regclass('public.document_extractions')::text;" \
  | grep -qw document_extractions \
  || { echo "[gate 1] FAIL: document_extractions table missing"; exit 1; }
psql_q -c "SELECT tgname FROM pg_trigger WHERE tgname='document_extractions_immutable';" \
  | grep -qw document_extractions_immutable \
  || { echo "[gate 1] FAIL: immutability trigger missing"; exit 1; }
echo "[gate 1] PASS"

# Migration-smoke SQL fixtures cover the trigger + unique-NULL semantics in detail.
echo "        (full DB invariants: bash apps/api/Tests/migration-smoke/AddDocumentStore.sql)"

start_api

# ── Gate 2: stateless POST /api/extract (Path A) ─────────────────────────
echo
echo "[gate 2] Stateless POST /api/extract returns populated fields"
RESP=$(curl -sf -F file=@"$PACKET_DIR/license.pdf" -F docType=license \
            "$API_URL/api/extract")
echo "$RESP" | jq -e '.fields.fullName | contains("Anderson")' >/dev/null \
  || { echo "[gate 2] FAIL: fullName not found in response: $RESP"; exit 1; }
echo "$RESP" | jq -e '.fields.state == "NY"' >/dev/null \
  || { echo "[gate 2] FAIL: state != NY in response"; exit 1; }
echo "[gate 2] PASS — fullName='$(echo "$RESP" | jq -r .fields.fullName)' state=NY"

# ── Gate 3: stateful POST /api/providers/{id}/documents (Path B) ─────────
echo
echo "[gate 3] Stateful POST /api/providers/{id}/documents persists row"

# Seed at least one provider if none exist. The Seed CLI wipes + reloads
# fixtures; idempotent across runs.
PROVIDER_COUNT=$(psql_q -c "SELECT COUNT(*) FROM providers;")
if (( ${PROVIDER_COUNT// /} == 0 )); then
  echo "        (no providers — running tools/Seed/ to load P1 fixtures)"
  dotnet run --project tools/Seed/Seed.csproj >/dev/null
fi
PROVIDER_ID=$(psql_q -c "SELECT id FROM providers ORDER BY created_at LIMIT 1;" | tr -d ' ')

UPLOAD=$(curl -sf -F file=@"$PACKET_DIR/license.pdf" \
              "$API_URL/api/providers/$PROVIDER_ID/documents")
DOC_ID=$(echo "$UPLOAD"     | jq -r .documentId)
DOC_TYPE=$(echo "$UPLOAD"   | jq -r .docType)
DOC_CONF=$(echo "$UPLOAD"   | jq -r .docTypeConfidence)
EXT_ID=$(echo "$UPLOAD"     | jq -r .extractionId)
CACHE_HIT=$(echo "$UPLOAD"  | jq -r .wasCacheHit)

[[ "$DOC_TYPE" == "license" ]] \
  || { echo "[gate 3] FAIL: docType=$DOC_TYPE (expected license)"; exit 1; }
[[ "$CACHE_HIT" == "false" ]] \
  || { echo "[gate 3] FAIL: wasCacheHit=true on first upload"; exit 1; }
echo "[gate 3] PASS — documentId=$DOC_ID extractionId=$EXT_ID confidence=$DOC_CONF"

# ── Gate 4: prompt files + per-extraction prompt_hash audit ─────────────
echo
echo "[gate 4] documents.classifier_prompt_hash + document_extractions.prompt_hash"
CLF_HASH=$(psql_q -c "SELECT classifier_prompt_hash FROM documents WHERE id='$DOC_ID';" | tr -d ' ')
EXT_HASH=$(psql_q -c "SELECT prompt_hash FROM document_extractions WHERE document_id='$DOC_ID' AND extraction_id=$EXT_ID;" | tr -d ' ')
[[ ${#CLF_HASH} -eq 64 ]] \
  || { echo "[gate 4] FAIL: classifier_prompt_hash len=${#CLF_HASH} (expected 64)"; exit 1; }
[[ ${#EXT_HASH} -eq 64 ]] \
  || { echo "[gate 4] FAIL: extraction prompt_hash len=${#EXT_HASH} (expected 64)"; exit 1; }
SCHEMA=$(psql_q -c "SELECT schema_version FROM document_extractions WHERE document_id='$DOC_ID' AND extraction_id=$EXT_ID;" | tr -d ' ')
[[ "$SCHEMA" == "license.v1" ]] \
  || { echo "[gate 4] FAIL: schema_version=$SCHEMA (expected license.v1)"; exit 1; }
echo "[gate 4] PASS — schema=$SCHEMA classifier_hash=${CLF_HASH:0:8}… extractor_hash=${EXT_HASH:0:8}…"

# ── Gate 5: idempotent reextract returns cache hit ───────────────────────
echo
echo "[gate 5] POST /api/documents/{id}/reextract is idempotent on (doc, schema, model, hash)"
REEXTRACT=$(curl -sf -X POST "$API_URL/api/documents/$DOC_ID/reextract")
NEW_EXT_ID=$(echo "$REEXTRACT"  | jq -r .extractionId)
NEW_CACHE=$(echo "$REEXTRACT"   | jq -r .wasCacheHit)
[[ "$NEW_EXT_ID" == "$EXT_ID" ]] \
  || { echo "[gate 5] FAIL: reextract returned extractionId=$NEW_EXT_ID (expected cache hit at $EXT_ID)"; exit 1; }
[[ "$NEW_CACHE" == "true" ]] \
  || { echo "[gate 5] FAIL: wasCacheHit=$NEW_CACHE on reextract (expected true)"; exit 1; }
EXT_COUNT=$(psql_q -c "SELECT COUNT(*) FROM document_extractions WHERE document_id='$DOC_ID';" | tr -d ' ')
[[ "$EXT_COUNT" == "1" ]] \
  || { echo "[gate 5] FAIL: $EXT_COUNT extractions for doc (expected 1 — reextract should not have inserted)"; exit 1; }
echo "[gate 5] PASS — one extraction, reextract cached"

# ── Gate 6: POST /api/providers/{id}/scores reads from extractions ───────
echo
echo "[gate 6] Score endpoint reads from extractions (aggregator path)"
SCORE=$(curl -sf -X POST "$API_URL/api/providers/$PROVIDER_ID/scores")

# The aggregator emits DocumentStore-Critical issues for each missing doc-type.
# This packet only uploaded a License, so DEA / BoardCert / Malpractice should
# each produce one — that's proof the score path goes through the aggregator
# and not the old hand-curated-profile path (the seed fixture has all four
# *Info records populated, so the pre-rewire handler would have emitted zero
# Missing-Document Issues).
HAS_DOCSTORE_CRITICAL=$(echo "$SCORE" | jq '[.issues[] | select(.validator == "DocumentStore" and .severity == "Critical")] | length')
(( HAS_DOCSTORE_CRITICAL >= 1 )) \
  || { echo "[gate 6] FAIL: no DocumentStore-Critical Issue; aggregator did not run"; exit 1; }

# Citation.documentId population on VALIDATOR-emitted Issues is unit-tested
# at LicenseStatusValidatorTests.CitationCarriesProvenance_…; the clean packet
# doesn't trigger any validator branch, so we don't re-assert it here.
echo "[gate 6] PASS — $HAS_DOCSTORE_CRITICAL DocumentStore-Critical issue(s) emitted by aggregator"

# ── Gate 7: eval runner produces non-zero accuracy table ─────────────────
echo
echo "[gate 7] Python eval runner produces non-zero accuracy + matches baseline"
LATEST=$(mktemp -t packetready-eval-latest.XXXXXX.json)
evals/generators/.venv/bin/python -m runners.run evals/dataset/ \
  --base-url "$API_URL" \
  --results "$LATEST" \
  --check-against evals/results/baseline.json >/dev/null
STUB=$(jq -r .stub "$LATEST")
[[ "$STUB" == "false" ]] \
  || { echo "[gate 7] FAIL: latest results stub=$STUB (expected false)"; exit 1; }
LIC_FN=$(jq -r '.rollups.perField["license.fullName"]' "$LATEST")
awk "BEGIN{exit !($LIC_FN > 0)}" \
  || { echo "[gate 7] FAIL: license.fullName=$LIC_FN (expected > 0)"; exit 1; }
echo "[gate 7] PASS — license.fullName=$LIC_FN, gate against baseline.json clean"

# ── Gate 8: citation drill-in (GET /blob streams the source PDF) ─────────
echo
echo "[gate 8] GET /api/documents/{id}/blob streams the source PDF"
BLOB=$(mktemp -t packetready-blob.XXXXXX.pdf)
curl -sf "$API_URL/api/documents/$DOC_ID/blob" -o "$BLOB"
# %PDF- magic at the start — proves it's a real PDF, not an error page.
head -c 5 "$BLOB" | grep -q '%PDF-' \
  || { echo "[gate 8] FAIL: response did not start with %PDF-"; exit 1; }
ORIG_SIZE=$(wc -c < "$PACKET_DIR/license.pdf")
GOT_SIZE=$(wc -c < "$BLOB")
[[ "$ORIG_SIZE" == "$GOT_SIZE" ]] \
  || { echo "[gate 8] FAIL: byte size $GOT_SIZE != original $ORIG_SIZE"; exit 1; }
rm -f "$BLOB" "$LATEST"
echo "[gate 8] PASS — $GOT_SIZE bytes round-tripped"

# ── Done ─────────────────────────────────────────────────────────────────
echo
echo "[gate-walk] ALL 8 GATES PASS"
