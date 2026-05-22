"""Orchestrate one eval pass over a packet dataset.

For each packet directory under `dataset_dir`:
  1. Load `golden.json`.
  2. POST each PDF to the extractor.
  3. Score returned fields vs. the golden labels.
Aggregate via `metrics.rollup` and write a results JSON.

CLI: `python -m runners.run evals/dataset/ --results evals/results/latest.json`

Fail-loud policy: any network error, HTTP error, or `ExtractorContractError`
aborts the run with a non-zero exit. Field-level misses do NOT abort — they
are the signal the eval is designed to measure.

Regression gate: with `--check-against <baseline.json>`, exits non-zero if any
per-field metric drops > REGRESSION_THRESHOLD_PP percentage points from
baseline. Honors `stub: true` on either side by skipping the comparison —
until P3 commits real numbers, the gate is a schema check, not a benchmark.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import asdict
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import httpx
from packetready_eval.schema import PER_FIELD_KEYS

from . import DEFAULT_BASE_URL
from .compare import FieldResult, compare_doc
from .extractor_client import ExtractorClient, ExtractorContractError
from .golden import load_and_validate
from .metrics import Rollups, rollup

REGRESSION_THRESHOLD_PP = 2.0
# Per-field rates are stored as round(x, 4); deltas land on a 0.0001 grid.
# Snap drop_pp to the same grid so 2.0000000000000018 != "> 2.0" any more.
_DROP_PP_DECIMALS = 4
# Both sides of the gate are also re-snapped to this resolution before the
# subtract, so an externally-written results file with floats at full 64-bit
# precision compares apples-to-apples with our own `_ratio` output.
_RATIO_DECIMALS = 4


def _normalize_ratio(value: float | None) -> float | None:
    """Snap an incoming per-field rate to the same grid `_ratio` emits.

    The gate is defined against the 4-decimal grid that `metrics._ratio`
    produces. A baseline or current payload written by another tool may
    carry full-precision floats; without normalization, those values can
    drift across the 2 pp boundary on otherwise-identical inputs.
    """
    if value is None:
        return None
    return round(float(value), _RATIO_DECIMALS)


def _evaluate_packet(
    packet_dir: Path,
    client: ExtractorClient,
) -> tuple[str, list[FieldResult]]:
    # Validate before opening a single PDF — fail loud on packet shape so a
    # malformed golden surfaces as "packet-003: documents[2] missing
    # 'filename'", not a KeyError mid-run.
    golden = load_and_validate(packet_dir / "golden.json")
    packet_id: str = golden["packetId"]

    results: list[FieldResult] = []
    for doc in golden["documents"]:
        extracted = client.extract(packet_dir / doc["filename"], doc["type"])
        results.extend(compare_doc(doc["type"], doc["fields"], extracted))
    return packet_id, results


def _packet_dirs(dataset_dir: Path) -> list[Path]:
    """Sorted subdirectories of `dataset_dir` that contain a golden.json.

    Sorting keeps the results JSON stable across runs and makes git diffs
    on baselines diff-friendly.
    """
    return sorted(
        p for p in dataset_dir.iterdir()
        if p.is_dir() and (p / "golden.json").is_file()
    )


def _results_payload(
    *,
    dataset_dir: Path,
    base_url: str,
    rollups: Rollups,
    per_packet_results: dict[str, list[FieldResult]],
    stub: bool,
) -> dict[str, Any]:
    return {
        "datasetDir": str(dataset_dir),
        "baseUrl": base_url,
        "ranAt": datetime.now(UTC).isoformat(timespec="seconds"),
        # `stub: true` marks "no real extractor numbers here." Set only when
        # --stub is passed explicitly. We intentionally do NOT infer it from
        # all-zero rollups: once P3 ships, a broken extractor that scores 0%
        # would silently masquerade as a stub run and bypass the gate.
        "stub": stub,
        "rollups": {
            "perField": rollups.per_field,
            "perDocType": rollups.per_doc_type,
            "perPacket": rollups.per_packet,
        },
        "packets": [
            {
                "packetId": packet_id,
                "fields": [asdict(r) for r in results],
            }
            for packet_id, results in per_packet_results.items()
        ],
    }


def check_against_baseline(current: dict[str, Any], baseline_path: Path) -> int:
    """Return 0 (pass) or 1 (fail).

    Skip the numeric comparison when either side is `stub: true`; in that
    case do a schema-only check that the locked per-field key set is present
    on both files. Once P3 commits real numbers, both sides become non-stub
    and the > 2 pp threshold becomes load-bearing automatically.

    A missing baseline path is treated as a hard failure: the caller passed
    `--check-against`, they wanted the gate to run.
    """
    if not baseline_path.exists():
        print(f"[gate] FAIL — baseline {baseline_path} does not exist")
        return 1

    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    current_pf = current["rollups"]["perField"]
    baseline_pf = baseline["rollups"]["perField"]

    if baseline.get("stub") or current.get("stub"):
        print("[gate] stub baseline detected — comparison skipped, schema-only check")
        missing_current = [k for k in PER_FIELD_KEYS if k not in current_pf]
        missing_baseline = [k for k in PER_FIELD_KEYS if k not in baseline_pf]
        if missing_current or missing_baseline:
            print(f"[gate] FAIL schema: missing current={missing_current} baseline={missing_baseline}")
            return 1
        print("[gate] PASS schema")
        return 0

    failed: list[str] = []
    for key in PER_FIELD_KEYS:
        b = _normalize_ratio(baseline_pf.get(key))
        c = _normalize_ratio(current_pf.get(key))
        if b is None or c is None:
            # An unobserved field after P3 is its own signal — treat as a miss
            # against any non-None baseline value, ignore otherwise.
            if b is not None and b > 0.0:
                failed.append(f"{key}: {b:.3f} → (no observation in current run)")
            continue
        drop_pp = round((b - c) * 100.0, _DROP_PP_DECIMALS)
        if drop_pp > REGRESSION_THRESHOLD_PP:
            failed.append(f"{key}: {b:.3f} → {c:.3f} (drop {drop_pp:.1f} pp)")

    if failed:
        print(f"[gate] FAIL — {len(failed)} per-field metrics dropped > {REGRESSION_THRESHOLD_PP} pp:")
        for line in failed:
            print(f"    {line}")
        return 1

    print(f"[gate] PASS — no per-field metric dropped > {REGRESSION_THRESHOLD_PP} pp from baseline")
    return 0


def run(
    dataset_dir: Path,
    *,
    base_url: str,
    results_path: Path,
    force_stub: bool = False,
) -> tuple[Rollups, dict[str, Any]]:
    """Execute the eval pass. Returns (rollups, payload); writes the full
    per-field detail to `results_path`."""
    packet_dirs = _packet_dirs(dataset_dir)
    if not packet_dirs:
        # An empty dataset usually means a wrong `dataset_dir` argument. Silent
        # "0 packets" runs produce all-None rollups that still pass the schema
        # gate, hiding the real problem. Fail loud instead.
        raise FileNotFoundError(
            f"no packet directories with golden.json under {dataset_dir} "
            f"(checked that {dataset_dir} exists and contains subdirectories)"
        )

    per_packet_results: dict[str, list[FieldResult]] = {}
    with ExtractorClient(base_url=base_url) as client:
        for packet_dir in packet_dirs:
            packet_id, results = _evaluate_packet(packet_dir, client)
            per_packet_results[packet_id] = results

    rollups = rollup(per_packet_results)

    results_path.parent.mkdir(parents=True, exist_ok=True)
    payload = _results_payload(
        dataset_dir=dataset_dir,
        base_url=base_url,
        rollups=rollups,
        per_packet_results=per_packet_results,
        stub=force_stub,
    )
    results_path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    return rollups, payload


_WORST_FIELDS_TO_SHOW = 3


def _format_summary(rollups: Rollups) -> str:
    def fmt(v: float | None) -> str:
        return "n/a" if v is None else f"{v:.2%}"

    lines = ["per doc-type:"]
    for k, v in rollups.per_doc_type.items():
        lines.append(f"  {k:<14} {fmt(v)}")
    lines.append("per packet:")
    for k, v in rollups.per_packet.items():
        lines.append(f"  {k:<32} {fmt(v)}")

    # Worst per-field rates are where debugging starts. Skip unobserved
    # fields — `None` isn't worse than a real `0.0`, just absent.
    observed = [(k, v) for k, v in rollups.per_field.items() if v is not None]
    if observed:
        worst = sorted(observed, key=lambda kv: kv[1])[:_WORST_FIELDS_TO_SHOW]
        lines.append(f"worst {len(worst)} per-field:")
        for k, v in worst:
            lines.append(f"  {k:<32} {fmt(v)}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run the PacketReady extractor eval.")
    parser.add_argument(
        "dataset_dir",
        type=Path,
        help="Directory containing packet subdirectories (e.g. evals/dataset/).",
    )
    parser.add_argument(
        "--base-url",
        default=DEFAULT_BASE_URL,
        help=f"Extractor base URL. Default: {DEFAULT_BASE_URL}",
    )
    parser.add_argument(
        "--results",
        type=Path,
        default=Path("evals/results/latest.json"),
        help="Where to write the results JSON. Default: evals/results/latest.json",
    )
    parser.add_argument(
        "--stub",
        action="store_true",
        help="Force-mark the results file as stub:true (used to write the initial baseline before P3).",
    )
    parser.add_argument(
        "--check-against",
        type=Path,
        default=None,
        help="Baseline results JSON; enables the regression gate (>2pp drop fails).",
    )
    args = parser.parse_args(argv)

    try:
        rollups, payload = run(
            args.dataset_dir,
            base_url=args.base_url,
            results_path=args.results,
            force_stub=args.stub,
        )
    except httpx.RequestError as exc:
        # Transport-layer faults — connect refused, DNS failure, read timeout.
        # Distinct from HTTP-status faults (which are server-reported and
        # bubble as HTTPStatusError); we want a one-line operator-facing
        # message here, not a 30-line traceback ending in "ConnectError".
        print(f"[run] FAIL — cannot reach extractor at {args.base_url}: {exc}", file=sys.stderr)
        return 1
    except httpx.HTTPStatusError as exc:
        print(f"[run] FAIL — extractor returned {exc.response.status_code} for {exc.request.url}", file=sys.stderr)
        return 1
    except ExtractorContractError as exc:
        print(f"[run] FAIL — extractor contract violation: {exc}", file=sys.stderr)
        return 1
    except FileNotFoundError as exc:
        print(f"[run] FAIL — {exc}", file=sys.stderr)
        return 1

    print(_format_summary(rollups))
    print(f"wrote results to {args.results}  (stub={payload['stub']})")

    if args.check_against:
        return check_against_baseline(payload, args.check_against)
    return 0


if __name__ == "__main__":
    sys.exit(main())
