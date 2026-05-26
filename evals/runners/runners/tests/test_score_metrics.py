"""Unit coverage for score_metrics.py helpers.

Pins:
  - load_planted_conflicts on a real golden + a missing-required-key reject
  - build_packet_result wire shape
  - build_score_result wire validation (score type + tier vocabulary)
  - human_tiers loader + mtime anchoring gate
  - measure_all skips agreement when n<2
  - baseline_payload omits agreement key entirely when None
"""

from __future__ import annotations

import json
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import cast

import pytest

from runners.agreement import ScoreResult, TierLabel
from runners.conflict_metrics import ConflictCount, EXPECTED_VALIDATOR, PacketResult
from runners.score_metrics import (
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


def _sr(packet_id: str, score: int, tier: str) -> ScoreResult:
    """Build a ScoreResult from a plain str tier — centralises the
    TierLabel narrowing so tests don't sprinkle `cast`/`type: ignore`."""
    return ScoreResult(packet_id, score, cast("TierLabel", tier))


# --- load_planted_conflicts ----------------------------------------------


def _write_golden(tmp_path: Path, planted: list[dict]) -> Path:
    golden = {
        "packetId": "packet-x",
        "documents": [],
        "plantedConflicts": planted,
    }
    p = tmp_path / "golden.json"
    p.write_text(json.dumps(golden), encoding="utf-8")
    return p


def test_load_planted_conflicts_happy(tmp_path: Path):
    p = _write_golden(tmp_path, [{
        "kind": "name_variant",
        "field": "malpractice.fullName",
        "sources": ["license", "malpractice"],
        "expected_to_flag": True,
        "shape": "HYPHENATED_SUFFIX",
        "expectedSeverity": "Critical",
    }])

    out = load_planted_conflicts(p)

    assert len(out) == 1
    assert out[0]["kind"] == "name_variant"
    # Optional shape rides along untouched.
    assert out[0]["shape"] == "HYPHENATED_SUFFIX"


def test_load_planted_conflicts_empty_when_key_absent(tmp_path: Path):
    # Clean-bucket packets have no plantedConflicts key.
    golden = {"packetId": "packet-clean", "documents": []}
    p = tmp_path / "golden.json"
    p.write_text(json.dumps(golden), encoding="utf-8")

    assert load_planted_conflicts(p) == ()


def test_load_planted_conflicts_rejects_missing_required_key(tmp_path: Path):
    # `expected_to_flag` omitted — the recall denominator would silently
    # default and quietly miss every typo-tolerance shape. Fail loud.
    p = _write_golden(tmp_path, [{
        "kind": "name_variant",
        "field": "malpractice.fullName",
        "sources": ["license", "malpractice"],
    }])

    with pytest.raises(ScoreEvalContractError, match="expected_to_flag"):
        load_planted_conflicts(p)


def test_load_planted_conflicts_rejects_non_list(tmp_path: Path):
    golden = {"packetId": "x", "documents": [], "plantedConflicts": "not a list"}
    p = tmp_path / "golden.json"
    p.write_text(json.dumps(golden), encoding="utf-8")

    with pytest.raises(ScoreEvalContractError, match="must be a list"):
        load_planted_conflicts(p)


def test_load_planted_conflicts_against_real_packet_003():
    # Sanity-check against the committed dataset so a future planter
    # schema change is noticed here, not by an eval run mid-CI.
    here = Path(__file__).resolve().parents[3]
    golden = here / "dataset" / "packet-003-conflict-name" / "golden.json"
    if not golden.exists():
        pytest.skip(f"packet-003 not present at {golden}")
    planted = load_planted_conflicts(golden)
    assert len(planted) == 1
    assert planted[0]["kind"] == "name_variant"
    assert planted[0]["expected_to_flag"] is True


# --- build_packet_result / build_score_result -----------------------------


def test_build_packet_result_wires_shape():
    score = {
        "score": 70,
        "tier": "Yellow",
        "issues": [
            {"validator": "identity_coherence",
             "field": "malpractice.fullName",
             "citations": [{"documentId": "doc-license"}]},
        ],
    }
    pr = build_packet_result(
        packet_id="packet-x",
        planted_conflicts=({"kind": "name_variant",
                            "field": "malpractice.fullName",
                            "sources": ["license", "malpractice"],
                            "expected_to_flag": True},),
        score_response=score,
        doc_type_by_doc_id={"doc-license": "license"},
    )

    assert isinstance(pr, PacketResult)
    assert pr.packet_id == "packet-x"
    assert len(pr.emitted_issues) == 1
    assert pr.doc_type_by_doc_id == {"doc-license": "license"}


def test_build_packet_result_rejects_missing_issues():
    with pytest.raises(ScoreEvalContractError, match="issues"):
        build_packet_result(
            packet_id="packet-x",
            planted_conflicts=(),
            score_response={"score": 100, "tier": "Green"},  # no `issues`
            doc_type_by_doc_id={},
        )


def test_build_score_result_happy():
    sr = build_score_result(
        packet_id="packet-x",
        score_response={"score": 85, "tier": "Green", "issues": []},
    )
    assert isinstance(sr, ScoreResult)
    assert sr.score == 85
    assert sr.tier == "Green"


def test_build_score_result_rejects_bogus_tier():
    with pytest.raises(ScoreEvalContractError, match="tier"):
        build_score_result(
            packet_id="packet-x",
            score_response={"score": 50, "tier": "Maybe", "issues": []},
        )


def test_build_score_result_rejects_non_int_score():
    with pytest.raises(ScoreEvalContractError, match="score"):
        build_score_result(
            packet_id="packet-x",
            score_response={"score": "high", "tier": "Green", "issues": []},
        )


# --- load_human_tiers ------------------------------------------------------


def _write_labels(tmp_path: Path, labels: dict[str, str], *, bias_note: str = "") -> Path:
    payload = {
        "_method": "test fixture",
        "_labeler": "test",
        "_biasNote": bias_note,
        "labels": labels,
    }
    p = tmp_path / "human_tiers.json"
    p.write_text(json.dumps(payload), encoding="utf-8")
    return p


def test_load_human_tiers_happy(tmp_path: Path):
    p = _write_labels(tmp_path, {"p1": "Green", "p2": "Yellow", "p3": "Red"},
                      bias_note="solo labeler — see README")

    ht = load_human_tiers(p)

    assert isinstance(ht, HumanTiers)
    assert ht.labels == {"p1": "Green", "p2": "Yellow", "p3": "Red"}
    assert "solo labeler" in ht.bias_note


def test_load_human_tiers_rejects_non_string_bias_note(tmp_path: Path):
    # _biasNote is a labeling-discipline artifact; silently dropping a
    # non-string value would lose that signal. Fail loud, same as the
    # other contract violations.
    payload = {
        "_biasNote": 42,
        "labels": {"p1": "Green"},
    }
    p = tmp_path / "human_tiers.json"
    p.write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(ScoreEvalContractError, match="_biasNote"):
        load_human_tiers(p)


def test_load_human_tiers_rejects_invalid_tier(tmp_path: Path):
    p = _write_labels(tmp_path, {"p1": "Maybe", "p2": "Green"})

    with pytest.raises(ScoreEvalContractError, match="Maybe"):
        load_human_tiers(p)


def test_load_human_tiers_rejects_empty_labels(tmp_path: Path):
    p = _write_labels(tmp_path, {})

    with pytest.raises(ScoreEvalContractError, match="non-empty"):
        load_human_tiers(p)


def test_load_human_tiers_missing_file_fails_loud(tmp_path: Path):
    bogus = tmp_path / "does-not-exist.json"
    with pytest.raises(FileNotFoundError, match="human_tiers.json"):
        load_human_tiers(bogus)


def test_load_human_tiers_mtime_gate_triggers_when_labels_newer(tmp_path: Path):
    # Labels file is newer than the baseline → anchoring gate fires.
    p = _write_labels(tmp_path, {"p1": "Green", "p2": "Yellow"})
    # Pretend the baseline was generated an hour ago.
    baseline_at = datetime.now(UTC) - timedelta(hours=1)

    with pytest.raises(LabelsMtimeError, match="anchoring gate"):
        load_human_tiers(p, baseline_generated_at=baseline_at)


def test_load_human_tiers_mtime_gate_passes_when_labels_older(tmp_path: Path):
    p = _write_labels(tmp_path, {"p1": "Green", "p2": "Yellow"})
    # Baseline generated NOW, labels written before. Pass.
    baseline_at = datetime.now(UTC) + timedelta(hours=1)

    ht = load_human_tiers(p, baseline_generated_at=baseline_at)
    assert ht.labels == {"p1": "Green", "p2": "Yellow"}


def test_load_human_tiers_no_gate_when_baseline_arg_none(tmp_path: Path):
    # Smoke/dev mode: gate is skipped when caller passes None.
    p = _write_labels(tmp_path, {"p1": "Green", "p2": "Yellow"})
    ht = load_human_tiers(p, baseline_generated_at=None)
    assert ht.labels == {"p1": "Green", "p2": "Yellow"}


# --- measure_all / baseline_payload ---------------------------------------


def _packet_with_catch() -> PacketResult:
    idx = {"doc-license": "license", "doc-malpractice": "malpractice"}
    inv = {v: k for k, v in idx.items()}
    return PacketResult(
        packet_id="p1",
        planted_conflicts=(
            {"kind": "name_variant", "field": "malpractice.fullName",
             "sources": ["license", "malpractice"], "expected_to_flag": True},
        ),
        emitted_issues=(
            {"validator": "identity_coherence",
             "field": "malpractice.fullName",
             "citations": [{"documentId": inv["license"]},
                           {"documentId": inv["malpractice"]}]},
        ),
        doc_type_by_doc_id=idx,
    )


def test_measure_all_with_labels():
    pr = _packet_with_catch()
    score_results = {
        "p1": _sr("p1", 30, "Red"),
        "p2": _sr("p2", 90, "Green"),
    }
    labels = {"p1": "Red", "p2": "Green"}

    counts, agreement = measure_all([pr], score_results, labels)

    assert counts["name_variant"].caught == 1
    assert agreement is not None
    assert agreement.n == 2
    assert agreement.weighted_kappa == 1.0


def test_measure_all_returns_none_agreement_when_labels_missing():
    counts, agreement = measure_all([_packet_with_catch()], {}, None)
    assert counts["name_variant"].caught == 1
    assert agreement is None


def test_measure_all_returns_none_agreement_when_aligned_n_below_2():
    pr = _packet_with_catch()
    counts, agreement = measure_all(
        [pr],
        score_results={"p1": _sr("p1", 30, "Red")},
        # Labels only annotate a packet the system didn't score → intersection = 0.
        labels={"p99": "Green"},
    )
    assert counts["name_variant"].caught == 1
    assert agreement is None


def test_baseline_payload_omits_agreement_when_none():
    counts = {kind: ConflictCount(kind=kind) for kind in EXPECTED_VALIDATOR}

    payload = baseline_payload(counts, agreement=None)

    assert "conflicts" in payload
    assert "agreement" not in payload   # explicit-omission contract


def test_baseline_payload_includes_both_when_agreement_present():
    counts = {kind: ConflictCount(kind=kind) for kind in EXPECTED_VALIDATOR}
    # Build a minimal AgreementMetrics via the existing helper.
    score_results = {
        "p1": _sr("p1", 30, "Red"),
        "p2": _sr("p2", 90, "Green"),
    }
    labels = {"p1": "Red", "p2": "Green"}
    _counts, agreement = measure_all([], score_results, labels)
    assert agreement is not None

    payload = baseline_payload(counts, agreement=agreement)

    assert "conflicts" in payload
    assert "agreement" in payload
    assert payload["agreement"]["n"] == 2
