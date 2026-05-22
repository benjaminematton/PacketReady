"""Pin the gate semantics:

  - exact 2.0 pp drop is a PASS (boundary, not a failure from FP rounding)
  - >2.0 pp drop is a FAIL
  - `stub: true` on either side short-circuits to schema-only
  - explicit `--check-against` on a missing file is a hard FAIL, not a skip
  - `--stub` is the only path to `stub: true` in the payload

These are the load-bearing invariants of the regression gate; they earn
their own test file even though it's short.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from packetready_eval.schema import PER_FIELD_KEYS

from runners.run import check_against_baseline, run


def _payload(per_field: dict[str, float | None], *, stub: bool = False) -> dict[str, Any]:
    full: dict[str, float | None] = {k: None for k in PER_FIELD_KEYS}
    full.update(per_field)
    return {"stub": stub, "rollups": {"perField": full}}


def _write(path: Path, payload: dict[str, Any]) -> Path:
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def test_exact_2pp_drop_passes(tmp_path: Path) -> None:
    # 1.00 → 0.98 is exactly 2.0 pp — the boundary case that the naive
    # `(b - c) * 100.0 > 2.0` form mis-classified as a fail.
    baseline = _write(tmp_path / "b.json", _payload({"license.state": 1.00}))
    current = _payload({"license.state": 0.98})
    assert check_against_baseline(current, baseline) == 0


def test_drop_above_2pp_fails(tmp_path: Path) -> None:
    baseline = _write(tmp_path / "b.json", _payload({"license.state": 1.00}))
    current = _payload({"license.state": 0.97})  # 3 pp drop
    assert check_against_baseline(current, baseline) == 1


def test_stub_either_side_skips_numeric_compare(tmp_path: Path) -> None:
    # A stub baseline with all-None per-field still has the full key set,
    # so schema check passes even when current would otherwise tank the gate.
    baseline = _write(tmp_path / "b.json", _payload({}, stub=True))
    current = _payload({"license.state": 0.00}, stub=False)
    assert check_against_baseline(current, baseline) == 0

    current_stub = _payload({"license.state": 0.00}, stub=True)
    baseline2 = _write(tmp_path / "b2.json", _payload({"license.state": 1.00}))
    assert check_against_baseline(current_stub, baseline2) == 0


def test_missing_baseline_file_is_hard_fail(tmp_path: Path) -> None:
    """Caller asked for the gate; if the file is gone, fail loud."""
    current = _payload({"license.state": 1.00})
    assert check_against_baseline(current, tmp_path / "does-not-exist.json") == 1


def test_baseline_observed_current_missing_is_fail(tmp_path: Path) -> None:
    """A field that scored real numbers in baseline but has no observation
    now is its own kind of regression."""
    baseline = _write(tmp_path / "b.json", _payload({"license.state": 1.00}))
    current = _payload({})  # license.state -> None
    assert check_against_baseline(current, baseline) == 1


def test_unnormalized_floats_compare_apples_to_apples(tmp_path: Path) -> None:
    """Values from an externally-written payload at full 64-bit precision
    must be snapped to the same 4-decimal grid `_ratio` produces, so the
    2 pp boundary stays deterministic across writers."""
    # 1.0 - 0.97999999999998 * 100 -> 2.000000000002 pp before normalization;
    # after _normalize_ratio rounds both sides to 4dp, the delta is 2.0 pp PASS.
    baseline = _write(tmp_path / "b.json", _payload({"license.state": 1.0}))
    current = _payload({"license.state": 0.97999999999998})
    assert check_against_baseline(current, baseline) == 0


def test_empty_dataset_dir_fails_loud(tmp_path: Path) -> None:
    """Wrong --dataset_dir is the most common runner misuse; a silent
    zero-packet run would pass the schema gate and mislead. Fail instead."""
    empty_dir = tmp_path / "no-packets"
    empty_dir.mkdir()
    with pytest.raises(FileNotFoundError):
        run(
            empty_dir,
            base_url="http://unused",
            results_path=tmp_path / "results.json",
            force_stub=True,
        )
