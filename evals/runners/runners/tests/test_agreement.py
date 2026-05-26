"""Unit coverage for agreement.py.

Pins the math against worked examples:
  - perfect agreement → κ=1.0, raw=1.0, ρ=1.0
  - all-wrong (Red↔Green swap) → negative κ, raw=0.0
  - one-step misses (Red↔Yellow) score better than two-step misses (Red↔Green)
  - degenerate "rater rates everything the same" → κ=0.0
  - tier vocabulary outside {Red, Yellow, Green} raises
  - confusion matrix orientation: rows = human, cols = system
  - n<2 raises
  - intersection-only: extra labels and extra score_results are dropped silently
  - Spearman ρ handles tied ranks (3-tier labels are tie-heavy by construction)
"""

from __future__ import annotations

import pytest

from runners.agreement import (
    AgreementMetrics,
    ScoreResult,
    baseline_payload,
    measure,
)


def _result(pid: str, score: int, tier: str) -> ScoreResult:
    return ScoreResult(packet_id=pid, score=score, tier=tier)  # type: ignore[arg-type]


def test_perfect_agreement_all_metrics_max():
    score_results = {
        "p1": _result("p1", 90, "Green"),
        "p2": _result("p2", 60, "Yellow"),
        "p3": _result("p3", 20, "Red"),
    }
    labels = {"p1": "Green", "p2": "Yellow", "p3": "Red"}

    m = measure(score_results, labels)

    assert m.weighted_kappa == 1.0
    assert m.raw_agreement == 1.0
    assert m.spearman_rho == 1.0
    assert m.n == 3
    # Diagonal-only confusion.
    assert m.confusion_3x3 == [
        [1, 0, 0],   # human Red → system Red
        [0, 1, 0],
        [0, 0, 1],
    ]


def test_one_step_miss_scores_higher_than_two_step_miss():
    # Quadratic weights: Red↔Yellow distance = 1, Red↔Green = 2. Penalty
    # ratio is 4×, so a dataset whose misses are all one-step should
    # produce a strictly higher κ than the same dataset with all misses
    # bumped to two-step. We need rater variance on both sides for κ to
    # be defined; six diagonal hits + two misses on each scenario keeps
    # the expected-agreement denominator non-zero.
    one_step_score_results = {}
    two_step_score_results = {}
    labels = {}
    # 6 diagonal hits spanning all three tiers.
    diag = [("Red", "Red"), ("Red", "Red"), ("Yellow", "Yellow"),
            ("Yellow", "Yellow"), ("Green", "Green"), ("Green", "Green")]
    for i, (h, s) in enumerate(diag):
        pid = f"d{i}"
        labels[pid] = h
        one_step_score_results[pid] = _result(pid, 50, s)
        two_step_score_results[pid] = _result(pid, 50, s)
    # 2 misses: one-step (Red→Yellow) vs two-step (Red→Green).
    labels["m1"] = "Red"
    labels["m2"] = "Red"
    one_step_score_results["m1"] = _result("m1", 50, "Yellow")
    one_step_score_results["m2"] = _result("m2", 50, "Yellow")
    two_step_score_results["m1"] = _result("m1", 50, "Green")
    two_step_score_results["m2"] = _result("m2", 50, "Green")

    one_step = measure(one_step_score_results, labels)
    two_step = measure(two_step_score_results, labels)

    assert one_step.weighted_kappa > two_step.weighted_kappa


def test_full_disagreement_red_green_swap_negative_kappa():
    # Half labeled Red, half Green; system inverts. κ should be sharply
    # negative — worse than chance.
    score_results = {}
    labels = {}
    for i in range(8):
        if i < 4:
            score_results[f"p{i}"] = _result(f"p{i}", 90, "Green")
            labels[f"p{i}"] = "Red"
        else:
            score_results[f"p{i}"] = _result(f"p{i}", 10, "Red")
            labels[f"p{i}"] = "Green"

    m = measure(score_results, labels)

    assert m.weighted_kappa < -0.5
    assert m.raw_agreement == 0.0


def test_system_rates_everything_yellow_zero_kappa():
    # Degenerate: no variance on the system side. Conventional κ is 0
    # (no chance-corrected improvement possible).
    score_results = {f"p{i}": _result(f"p{i}", 50, "Yellow") for i in range(6)}
    labels = {
        "p0": "Red", "p1": "Yellow", "p2": "Green",
        "p3": "Red", "p4": "Yellow", "p5": "Green",
    }

    m = measure(score_results, labels)

    # System never matches Red or Green, only Yellow → 2/6 raw.
    assert m.raw_agreement == round(2 / 6, 4)
    # Constant system column → expected agreement equals observed under
    # the all-Yellow column, so κ = 0.
    assert m.weighted_kappa == 0.0


def test_confusion_matrix_orientation_rows_are_human():
    # Two packets: human=Red→system=Yellow, human=Green→system=Yellow.
    # Both rows should have their Yellow column populated.
    score_results = {
        "p1": _result("p1", 50, "Yellow"),
        "p2": _result("p2", 50, "Yellow"),
    }
    labels = {"p1": "Red", "p2": "Green"}

    m = measure(score_results, labels)

    assert m.confusion_3x3[0][1] == 1   # human Red, system Yellow
    assert m.confusion_3x3[2][1] == 1   # human Green, system Yellow
    assert m.confusion_3x3[0][0] == 0   # no human=Red, system=Red
    assert m.confusion_3x3[2][2] == 0


def test_invalid_label_raises():
    score_results = {"p1": _result("p1", 50, "Yellow"), "p2": _result("p2", 90, "Green")}
    labels = {"p1": "Maybe", "p2": "Green"}

    with pytest.raises(ValueError, match="Maybe"):
        measure(score_results, labels)


def test_invalid_system_tier_raises():
    score_results = {
        "p1": _result("p1", 50, "Yellow"),
        "p2": _result("p2", 50, "Bogus"),  # type: ignore[arg-type]
    }
    labels = {"p1": "Yellow", "p2": "Yellow"}

    with pytest.raises(ValueError, match="Bogus"):
        measure(score_results, labels)


def test_n_lt_2_raises():
    score_results = {"p1": _result("p1", 50, "Yellow")}
    labels = {"p1": "Yellow"}

    with pytest.raises(ValueError, match="at least 2"):
        measure(score_results, labels)


def test_intersection_only_extras_silently_dropped():
    # Labeler annotated a packet the system didn't score; runner scored
    # a packet the labeler didn't see. Intersection is 2.
    score_results = {
        "p1": _result("p1", 50, "Yellow"),
        "p2": _result("p2", 90, "Green"),
        "p3": _result("p3", 10, "Red"),   # not in labels
    }
    labels = {
        "p1": "Yellow",
        "p2": "Green",
        "p4": "Red",                       # not in score_results
    }

    m = measure(score_results, labels)

    assert m.n == 2


def test_spearman_handles_ties():
    # 3-tier labels guarantee ties; the ranker must average tied ranks
    # (scipy's default) so ρ stays in [-1, 1] and reflects monotone trend.
    # Tie compression on the label side means ρ doesn't quite hit 1.0
    # even for perfectly monotone scores within each tier — this matches
    # scipy.stats.spearmanr's behavior on the same input. We pin a
    # plausibly-high lower bound rather than the exact value, so a
    # legitimate switch to scipy in the future doesn't break the test.
    score_results = {
        "p1": _result("p1", 10, "Red"),
        "p2": _result("p2", 20, "Red"),
        "p3": _result("p3", 60, "Yellow"),
        "p4": _result("p4", 70, "Yellow"),
        "p5": _result("p5", 90, "Green"),
        "p6": _result("p6", 95, "Green"),
    }
    labels = {"p1": "Red", "p2": "Red", "p3": "Yellow", "p4": "Yellow", "p5": "Green", "p6": "Green"}

    m = measure(score_results, labels)

    assert m.spearman_rho > 0.9


def test_spearman_zero_rank_variance_returns_zero():
    # Every label is Yellow → human ords are constant → rank variance
    # zero. Don't NaN out; return 0.0.
    score_results = {f"p{i}": _result(f"p{i}", i * 10, "Yellow") for i in range(4)}
    labels = {f"p{i}": "Yellow" for i in range(4)}

    m = measure(score_results, labels)

    assert m.spearman_rho == 0.0


def test_baseline_payload_shape():
    m = AgreementMetrics(
        weighted_kappa=0.62,
        raw_agreement=0.70,
        confusion_3x3=[[3, 1, 0], [0, 4, 2], [0, 1, 9]],
        spearman_rho=0.81,
        n=20,
    )

    payload = baseline_payload(m)

    assert payload == {
        "weightedKappa": 0.62,
        "rawAgreement": 0.70,
        "confusion3x3": [[3, 1, 0], [0, 4, 2], [0, 1, 9]],
        "spearmanRho": 0.81,
        "n": 20,
    }


def test_kappa_landis_koch_substantial_case():
    # Synthetic 20-packet result where κ ≈ 0.5: most pairs match, some
    # one-step misses, no two-step misses. Pins the "P4 DoD floor 0.50"
    # case so a regression in the weighting accidentally drifts the
    # threshold.
    score_results = {}
    labels = {}
    # 12 diagonal hits, 4 one-step misses, 4 distributed misses.
    hits = [
        ("Red", "Red"), ("Red", "Red"), ("Red", "Red"), ("Red", "Red"),
        ("Yellow", "Yellow"), ("Yellow", "Yellow"), ("Yellow", "Yellow"), ("Yellow", "Yellow"),
        ("Green", "Green"), ("Green", "Green"), ("Green", "Green"), ("Green", "Green"),
        ("Red", "Yellow"), ("Yellow", "Red"),
        ("Yellow", "Green"), ("Green", "Yellow"),
        ("Red", "Yellow"), ("Yellow", "Red"),
        ("Yellow", "Green"), ("Green", "Yellow"),
    ]
    for i, (human, system) in enumerate(hits):
        pid = f"p{i:02d}"
        score_results[pid] = _result(pid, 50, system)
        labels[pid] = human

    m = measure(score_results, labels)

    # κ in a substantial-agreement band. The exact value is pinned to
    # within a wide tolerance — the point is to catch a regression that
    # flips the sign or scales by an order of magnitude.
    assert 0.4 <= m.weighted_kappa <= 0.8
    assert m.raw_agreement == round(12 / 20, 4)
