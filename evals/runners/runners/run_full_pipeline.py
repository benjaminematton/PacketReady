"""Orchestrate the score-side eval pipeline (P4 task 18).

For each packet under ``dataset_dir``:

  1. Read ``golden.json``: pluck out the ``identity`` block and the
     ``plantedConflicts`` list (slice 2 of this chain).
  2. POST /api/providers with the identity + payerId (slice 1) →
     provider id.
  3. POST /api/providers/{id}/documents per PDF → record each
     documentId + its docType into a per-packet index.
  4. POST /api/providers/{id}/scores → ReadinessScoreDto.
  5. Hand the (planted, score-response, doc-index) triple to
     ``score_metrics.build_packet_result`` and the score-response to
     ``build_score_result``.

Packets run in parallel under a `ThreadPoolExecutor` (default
concurrency=3 — tuned for Anthropic Tier-2 headroom per the orchestrator
design notes). Each worker owns its own PipelineClient retry budget;
the 429-backoff loop inside PipelineClient absorbs rate limits without
the orchestrator having to know about them.

When ``labels_path`` is provided, the run also folds human-labeled
tiers through ``score_metrics.measure_all`` to produce the
``agreement`` block. The anchoring mtime gate requires the labels to
predate the supplied ``baseline_generated_at`` — see
``load_human_tiers``.

**No auto-wipe.** This module does not delete prior eval state. Runs
accumulate Provider/Document/Extraction rows in the dev DB; if you
want a clean slate, run ``dotnet run --project tools/Seed`` first (it
wipes-then-seeds the fixture providers). The orchestrator-side wipe
endpoint is deferred to a future slice — the cost is per-run row
growth in the dev DB, not correctness.

CLI:

    python -m runners.run_full_pipeline evals/dataset/ \\
        --results evals/results/latest.json \\
        --labels evals/labels/human_tiers.json \\
        --concurrency 3
"""

from __future__ import annotations

import argparse
import json
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import httpx
from packetready_eval.schema import DOC_FILENAMES

from . import DEFAULT_BASE_URL
from .agreement import ScoreResult
from .conflict_metrics import PacketResult
from .pipeline_client import (
    PipelineClient,
    PipelineContractError,
    RetryConfig,
)
from .score_metrics import (
    HumanTiers,
    LabelsMtimeError,
    ScoreEvalContractError,
    baseline_payload,
    build_packet_result,
    build_score_result,
    load_human_tiers,
    load_planted_conflicts,
    measure_all,
)

DEFAULT_PAYER_ID = "payer-a-national-hmo"


@dataclass(frozen=True)
class _PerPacketRun:
    """One packet's outputs from the orchestrator's worker. Folded
    into the run-level rollup after all workers finish."""
    packet_id: str
    packet_result: PacketResult
    score_result: ScoreResult


@dataclass(frozen=True)
class _PerPacketError:
    """One packet's failure mode. The orchestrator surfaces a count
    + the per-packet messages in the results payload; field-accuracy
    runs already follow this pattern, so the regression-gate side
    doesn't need to change shape to read score-side errors."""
    packet_id: str
    error_type: str
    message: str


def _packet_dirs(dataset_dir: Path) -> list[Path]:
    """Sorted subdirectories of ``dataset_dir`` that contain a
    ``golden.json``. Matches the field-accuracy runner so a packet
    that's valid for one is valid for the other."""
    if not dataset_dir.exists():
        raise FileNotFoundError(
            f"dataset directory does not exist: {dataset_dir}")
    return sorted(
        p for p in dataset_dir.iterdir()
        if p.is_dir() and (p / "golden.json").is_file()
    )


def _read_identity_and_payer(
    golden_path: Path,
    *,
    default_payer_id: str,
) -> tuple[dict[str, Any], str]:
    """Pull the identity block + payer id out of golden.json.

    payerId on goldens is optional today — the orchestrator falls
    back to ``default_payer_id`` (P4 single-payer decision). slice 2
    requires the identity block on every packet; missing surfaces as
    ScoreEvalContractError so a generator regression fails fast.
    """
    raw = json.loads(golden_path.read_text(encoding="utf-8"))
    identity = raw.get("identity")
    if not isinstance(identity, dict):
        raise ScoreEvalContractError(
            f"{golden_path}: missing or non-object `identity` block. "
            f"Slice 2 made this required — regen via `python -m "
            f"packetready_eval.packets` and confirm goldenSchemaVersion>=2."
        )
    payer_id = raw.get("payerId", default_payer_id)
    if not isinstance(payer_id, str) or not payer_id.strip():
        payer_id = default_payer_id
    return identity, payer_id


def _process_packet(
    packet_dir: Path,
    *,
    base_url: str,
    payer_id_default: str,
    retry: RetryConfig | None,
) -> _PerPacketRun:
    """Run the full sequence for a single packet. The PipelineClient
    is per-worker (per-packet) so connection pools don't get shared
    across threads — that's the cleanest async hygiene with a sync
    httpx.Client. The cost is one new TCP connection per packet,
    which is rounding error against ~30s of Sonnet latency per
    packet."""
    packet_id = packet_dir.name
    golden_path = packet_dir / "golden.json"

    identity, payer_id = _read_identity_and_payer(
        golden_path, default_payer_id=payer_id_default)
    planted = load_planted_conflicts(golden_path)

    with PipelineClient(base_url=base_url, retry=retry) as client:
        provider = client.create_provider(payer_id=payer_id, identity=identity)

        # Per-packet documentId → docType map. Built as we upload, then
        # threaded into build_packet_result so the conflict-metrics
        # source-overlap predicate can resolve citations back to docType.
        doc_type_by_doc_id: dict[str, str] = {}
        for doc_type, filename in DOC_FILENAMES.items():
            pdf_path = packet_dir / filename
            if not pdf_path.exists():
                raise ScoreEvalContractError(
                    f"{packet_id}: expected {filename} alongside golden.json"
                )
            up = client.upload_document(
                provider_id=provider.provider_id,
                pdf_path=pdf_path,
                doc_type=doc_type,
            )
            doc_type_by_doc_id[up.document_id] = up.doc_type

        score = client.compute_score(provider.provider_id)

    return _PerPacketRun(
        packet_id=packet_id,
        packet_result=build_packet_result(
            packet_id=packet_id,
            planted_conflicts=planted,
            score_response=score,
            doc_type_by_doc_id=doc_type_by_doc_id,
        ),
        score_result=build_score_result(
            packet_id=packet_id,
            score_response=score,
        ),
    )


def run(
    dataset_dir: Path,
    *,
    base_url: str = DEFAULT_BASE_URL,
    payer_id: str = DEFAULT_PAYER_ID,
    concurrency: int = 3,
    labels_path: Path | None = None,
    baseline_generated_at: datetime | None = None,
    retry: RetryConfig | None = None,
    progress: bool = False,
) -> dict[str, Any]:
    """Execute the full pipeline. Returns the assembled results payload
    (caller writes it to disk). Errors at the packet level are absorbed
    into the payload's `errors` list; a transport-level failure
    propagates to the caller untouched.

    ``progress=True`` emits one stderr line per completed packet so a
    long run (50 packets × ~30s = ~25min) isn't a silent stare.
    """
    packet_dirs = _packet_dirs(dataset_dir)
    if not packet_dirs:
        raise FileNotFoundError(
            f"no packet directories under {dataset_dir} (looked for "
            f"subdirs containing golden.json)")

    # Labels load up front so the mtime gate fails before we spend
    # ~25min on a run whose agreement block we'd refuse to publish.
    human_tiers: HumanTiers | None = None
    if labels_path is not None:
        human_tiers = load_human_tiers(
            labels_path, baseline_generated_at=baseline_generated_at)

    runs: list[_PerPacketRun] = []
    errors: list[_PerPacketError] = []

    started_at = datetime.now(UTC)

    # ThreadPoolExecutor matches the sync PipelineClient cleanly.
    # We don't reuse one client across threads — see _process_packet's
    # docstring for the rationale.
    total = len(packet_dirs)
    completed = 0
    with ThreadPoolExecutor(max_workers=max(1, concurrency)) as pool:
        futures = {
            pool.submit(
                _process_packet,
                pd,
                base_url=base_url,
                payer_id_default=payer_id,
                retry=retry,
            ): pd
            for pd in packet_dirs
        }
        for fut in as_completed(futures):
            pd = futures[fut]
            try:
                runs.append(fut.result())
                outcome = "ok"
            except (PipelineContractError, ScoreEvalContractError) as exc:
                errors.append(_PerPacketError(
                    packet_id=pd.name,
                    error_type=type(exc).__name__,
                    message=str(exc),
                ))
                outcome = f"FAIL {type(exc).__name__}"
            except httpx.RequestError as exc:
                # Transport / connect / timeout faults. We surface but
                # don't retry — the inner client already retried 429s,
                # and a connect refusal usually means the API is down.
                errors.append(_PerPacketError(
                    packet_id=pd.name,
                    error_type="HttpRequestError",
                    message=f"{type(exc).__name__}: {exc}",
                ))
                outcome = f"FAIL {type(exc).__name__}"
            completed += 1
            if progress:
                print(
                    f"[{completed}/{total}] {pd.name} {outcome}",
                    file=sys.stderr,
                    flush=True,
                )

    finished_at = datetime.now(UTC)

    # Deterministic order on rollup so two runs diff-able.
    runs.sort(key=lambda r: r.packet_id)
    errors.sort(key=lambda e: e.packet_id)

    score_results = {r.packet_id: r.score_result for r in runs}
    packet_results = [r.packet_result for r in runs]

    labels = dict(human_tiers.labels) if human_tiers is not None else None
    counts, agreement = measure_all(packet_results, score_results, labels)

    # Conflicts always present (empty dict if no kinds tracked yet);
    # agreement omitted when None so a consumer can fail-loud on the
    # missing key rather than silently consuming a stub.
    metrics = baseline_payload(counts, agreement)
    if "agreement" in metrics and human_tiers is not None:
        # Surface the labeler's provenance note so a regression-gate
        # consumer can show it without re-reading human_tiers.json.
        metrics["agreement"]["biasNote"] = human_tiers.bias_note

    payload: dict[str, Any] = {
        "datasetDir": str(dataset_dir),
        "baseUrl": base_url,
        "startedAt": started_at.isoformat(timespec="seconds"),
        "finishedAt": finished_at.isoformat(timespec="seconds"),
        "durationSeconds": round((finished_at - started_at).total_seconds(), 3),
        "packetCount": len(packet_dirs),
        "successCount": len(runs),
        "errorCount": len(errors),
        "errors": [
            {"packetId": e.packet_id, "errorType": e.error_type, "message": e.message}
            for e in errors
        ],
        "conflicts": metrics["conflicts"],
        "scoreResults": [
            {
                "packetId": r.packet_id,
                "score": r.score_result.score,
                "tier": r.score_result.tier,
            }
            for r in runs
        ],
    }
    if "agreement" in metrics:
        payload["agreement"] = metrics["agreement"]
    return payload


def _write_payload(payload: dict[str, Any], path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def _format_summary(payload: dict[str, Any]) -> str:
    lines: list[str] = []
    lines.append(
        f"packets: {payload['successCount']} ok, "
        f"{payload['errorCount']} error  ({payload['packetCount']} total)")
    if payload["errorCount"]:
        for e in payload["errors"][:5]:
            lines.append(f"  ! {e['packetId']}: {e['errorType']} — {e['message'][:120]}")
        if payload["errorCount"] > 5:
            lines.append(f"  … +{payload['errorCount'] - 5} more errors")
    lines.append("conflicts:")
    for kind, entry in payload.get("conflicts", {}).items():
        p = entry.get("precision")
        r = entry.get("recall")
        lines.append(
            f"  {kind:<32} planted={entry['planted']:<3} caught={entry['caught']:<3} "
            f"fabricated={entry['fabricated']:<3} P={p}  R={r}")
    if "agreement" in payload:
        a = payload["agreement"]
        lines.append(
            f"agreement (n={a['n']}): "
            f"κ={a['weightedKappa']}  raw={a['rawAgreement']}  ρ={a['spearmanRho']}")
        if a.get("biasNote"):
            lines.append(f"  bias note: {a['biasNote']}")
    lines.append(
        f"timing: {payload.get('durationSeconds', '?')}s "
        f"({payload.get('startedAt', '?')} → {payload.get('finishedAt', '?')})")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Run the PacketReady full-pipeline (score-side) eval.")
    parser.add_argument("dataset_dir", type=Path,
                        help="Packet dataset root (e.g. evals/dataset/).")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL,
                        help=f"API base URL. Default: {DEFAULT_BASE_URL}")
    parser.add_argument("--payer-id", default=DEFAULT_PAYER_ID,
                        help=f"PayerId to assign to created providers. "
                             f"Default: {DEFAULT_PAYER_ID}")
    parser.add_argument("--concurrency", type=int, default=3,
                        help="Packet-level concurrency (default 3 — tune by "
                             "Anthropic tier; see slice docs).")
    parser.add_argument("--results", type=Path,
                        default=Path("evals/results/full-pipeline-latest.json"),
                        help="Where to write the results JSON.")
    parser.add_argument("--labels", type=Path, default=None,
                        help="Optional human_tiers.json for agreement metrics.")
    parser.add_argument("--baseline-generated-at", type=str, default=None,
                        help="ISO datetime; enables the labels-mtime anchoring "
                             "gate. Omit to skip (smoke runs).")
    args = parser.parse_args(argv)

    baseline_at: datetime | None = None
    if args.baseline_generated_at is not None:
        baseline_at = datetime.fromisoformat(args.baseline_generated_at)

    try:
        payload = run(
            args.dataset_dir,
            base_url=args.base_url,
            payer_id=args.payer_id,
            concurrency=args.concurrency,
            labels_path=args.labels,
            baseline_generated_at=baseline_at,
            progress=True,
        )
    # Setup-level errors only. FileNotFoundError comes from missing
    # dataset/labels paths; LabelsMtimeError from the anchoring gate;
    # ScoreEvalContractError at this layer can only originate in
    # load_human_tiers (per-packet contract errors are absorbed into
    # payload.errors inside run(), not raised here).
    except (FileNotFoundError, LabelsMtimeError, ScoreEvalContractError) as exc:
        print(f"[run] FAIL — {exc}", file=sys.stderr)
        return 1

    _write_payload(payload, args.results)
    print(_format_summary(payload))
    print(f"\nwrote results to {args.results}")
    return 0 if payload["errorCount"] == 0 else 2


if __name__ == "__main__":
    sys.exit(main())
