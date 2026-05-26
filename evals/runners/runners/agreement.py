"""Score-vs-tier agreement metrics on the hand-labeled subset.

Replaces the earlier ``correlation.py`` plan. Spearman ρ is wrong as the
headline for 3-tier categorical labels at n=20 — ties dominate, ρ
destabilizes, p-values become uninterpretable. Quadratic-weighted
Cohen's κ is the standard ordinal-categorical agreement metric: it
gives partial credit for "off by one tier" misses and penalizes "off
by two" much more heavily.

Headline metrics:

  - **weighted_kappa**: quadratic-weighted Cohen's κ. Disagreement
    weights scale as ``(distance / max_distance)² = (|i - j| / 2)²``
    on a 3-tier scale. Landis-Koch substantial-agreement floor is
    0.61; the P4 DoD sets the floor at 0.50 because the labeler is
    solo and the structural bias (validator-designer-also-labels)
    means κ overstates ground-truth tracking.

  - **raw_agreement**: ``sum(system_tier == human_tier) / n``. A
    sanity check next to κ; κ corrects for chance, raw doesn't.

  - **confusion_3x3**: rows are human tier, columns are system tier,
    cells are counts. The shape most readers actually want to see.

  - **spearman_rho**: kept as a footnote per the design-doc continuity
    note, but explicitly not the headline. We hand-roll it (rank
    correlation on (score, ordinal human tier)) so the module stays
    dependency-free; scipy would be one import + six lines but the
    runner package doesn't otherwise need it.

The runner refuses to compute these metrics unless ``human_tiers.json``
was written **before** the corresponding ``baseline.json``'s
``generatedAt`` (anchoring mtime gate). That check lives in the runner
glue, not here — this module is a pure function over already-validated
inputs.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from typing import Literal

TierLabel = Literal["Red", "Yellow", "Green"]

# Ordinal mapping: Red is worst (most blockers), Green is ready. The
# distance between Red and Green is 2, which sets the kappa weight
# denominator.
TIER_TO_ORD: Mapping[str, int] = {"Red": 0, "Yellow": 1, "Green": 2}
ORD_TO_TIER: Mapping[int, str] = {v: k for k, v in TIER_TO_ORD.items()}
_TIER_COUNT = len(TIER_TO_ORD)


@dataclass(frozen=True)
class ScoreResult:
    """Per-packet system output used by the agreement runner. The runner
    builds these from POST /api/providers/{id}/scores responses; this
    module accepts them as plain data so unit tests can stub them
    without a network call."""
    packet_id: str
    score: int                       # 0-100 integer score
    tier: TierLabel                  # one of Red / Yellow / Green


@dataclass(frozen=True)
class AgreementMetrics:
    weighted_kappa: float            # quadratic weights; headline number
    raw_agreement: float             # count(system == human) / n
    confusion_3x3: list[list[int]]   # rows = human tier, cols = system tier
    spearman_rho: float              # footnote only — continuous score vs ordinal human tier
    n: int


def measure(
    score_results: Mapping[str, ScoreResult],
    labels: Mapping[str, str],
) -> AgreementMetrics:
    """Compute agreement metrics over the intersection of packet ids.

    Args:
        score_results: packet_id → ScoreResult.
        labels: packet_id → tier string (``"Red"`` | ``"Yellow"`` | ``"Green"``).

    Returns:
        AgreementMetrics. ``n`` is the size of the intersection — packets
        in either map but not the other are silently skipped. A typical
        cause is a labeler annotating a packet that's since been removed
        from the dataset; the runner is expected to surface the count
        delta separately.

    Raises:
        ValueError: an aligned packet carries a tier string outside the
            three-tier vocabulary, OR fewer than 2 aligned packets exist
            (κ is undefined on a singleton). Both fail loud — a silent
            zero or NaN would be a worse default than refusing to compute.
    """
    aligned_ids = sorted(set(score_results) & set(labels))
    n = len(aligned_ids)
    if n < 2:
        raise ValueError(
            f"agreement.measure requires at least 2 aligned packets; got {n} "
            f"(score_results={len(score_results)}, labels={len(labels)})."
        )

    human_ords: list[int] = []
    system_ords: list[int] = []
    scores: list[int] = []
    for pid in aligned_ids:
        human_label = labels[pid]
        if human_label not in TIER_TO_ORD:
            raise ValueError(
                f"label for packet {pid!r} is {human_label!r}; "
                f"must be one of {sorted(TIER_TO_ORD)}."
            )
        system = score_results[pid]
        if system.tier not in TIER_TO_ORD:
            raise ValueError(
                f"system tier for packet {pid!r} is {system.tier!r}; "
                f"must be one of {sorted(TIER_TO_ORD)}."
            )
        human_ords.append(TIER_TO_ORD[human_label])
        system_ords.append(TIER_TO_ORD[system.tier])
        scores.append(system.score)

    confusion = _build_confusion(human_ords, system_ords)
    raw = _raw_agreement(human_ords, system_ords)
    kappa = _quadratic_weighted_kappa(confusion)
    rho = _spearman_rho(scores, human_ords)

    return AgreementMetrics(
        weighted_kappa=round(kappa, 4),
        raw_agreement=round(raw, 4),
        confusion_3x3=confusion,
        spearman_rho=round(rho, 4),
        n=n,
    )


def baseline_payload(m: AgreementMetrics) -> dict:
    """Serialize to the shape :file:`baseline.json` carries under the
    ``agreement`` key. Stable key ordering for diff-readability."""
    return {
        "weightedKappa": m.weighted_kappa,
        "rawAgreement": m.raw_agreement,
        "confusion3x3": m.confusion_3x3,
        "spearmanRho": m.spearman_rho,
        "n": m.n,
    }


# --- helpers --------------------------------------------------------------


def _build_confusion(human: list[int], system: list[int]) -> list[list[int]]:
    """3×3 count matrix. Rows are human tier (0..2), cols are system tier."""
    m = [[0, 0, 0] for _ in range(_TIER_COUNT)]
    for h, s in zip(human, system):
        m[h][s] += 1
    return m


def _raw_agreement(human: list[int], system: list[int]) -> float:
    """Identity match rate. No partial credit; reported next to κ as a
    sanity check (κ corrects for chance, raw doesn't)."""
    matches = sum(1 for h, s in zip(human, system) if h == s)
    return matches / len(human)


def _quadratic_weighted_kappa(confusion: list[list[int]]) -> float:
    """Cohen's κ with quadratic weights on a fixed ordinal scale.

    Implementation per Cohen (1968):
        κ = 1 - sum(w_ij * O_ij) / sum(w_ij * E_ij)
    where:
        w_ij = ((i - j) / (k - 1))²   (quadratic on k-tier scale)
        O    = confusion / n          (observed proportions)
        E    = (row_marginal * col_marginal) / n²  (expected under independence)

    Degenerate cases:
      - all observations land in one cell (perfect agreement)
        → numerator and denominator both 0; return 1.0 (perfect).
      - one rater has zero variance (e.g. system rates everything Yellow)
        → denominator is 0 with non-zero numerator; return 0.0 (no agreement
        better than chance, which is undefined here).
    """
    k = _TIER_COUNT
    n = sum(sum(row) for row in confusion)
    if n == 0:
        return 0.0

    # Marginals.
    row_sums = [sum(row) for row in confusion]
    col_sums = [sum(confusion[i][j] for i in range(k)) for j in range(k)]

    # Weights on the [0, 1] grid: 0 on the diagonal, 1 at the extremes.
    denom_sq = (k - 1) ** 2
    weights = [
        [((i - j) ** 2) / denom_sq for j in range(k)]
        for i in range(k)
    ]

    weighted_observed = sum(
        weights[i][j] * confusion[i][j]
        for i in range(k) for j in range(k)
    )
    weighted_expected = sum(
        weights[i][j] * (row_sums[i] * col_sums[j] / n)
        for i in range(k) for j in range(k)
    )

    if weighted_expected == 0.0:
        return 1.0 if weighted_observed == 0.0 else 0.0
    return 1.0 - (weighted_observed / weighted_expected)


def _spearman_rho(x: list[int], y: list[int]) -> float:
    """Spearman ρ between two equal-length sequences via ranking.

    Hand-rolled so the runner stays scipy-free. The continuous-vs-ordinal
    asymmetry is fine for Spearman because both sides get ranked
    independently; rank ties are averaged (the standard
    fractional-rank tie-break). Equivalent to ``scipy.stats.spearmanr``
    up to floating-point noise on small inputs.

    Returns 0.0 when either side has zero rank variance — the Pearson
    formula on ranks divides by zero in that case, and "perfectly
    correlated to a constant" is a meaningless number to publish.
    """
    n = len(x)
    if n < 2 or len(y) != n:
        return 0.0

    rx = _average_ranks(x)
    ry = _average_ranks(y)

    mean_rx = sum(rx) / n
    mean_ry = sum(ry) / n
    cov = sum((a - mean_rx) * (b - mean_ry) for a, b in zip(rx, ry))
    var_x = sum((a - mean_rx) ** 2 for a in rx)
    var_y = sum((b - mean_ry) ** 2 for b in ry)
    denom = (var_x * var_y) ** 0.5
    if denom == 0.0:
        return 0.0
    return cov / denom


def _average_ranks(values: list[int]) -> list[float]:
    """Fractional-rank-style ranking: ties get the average of the ranks
    they would have occupied. Matches scipy.stats.rankdata's default
    method='average'. Ranks are 1-based, but the absolute base cancels
    in ρ — kept 1-based for diffability against published worked examples.
    """
    indexed = sorted(range(len(values)), key=lambda i: values[i])
    ranks = [0.0] * len(values)
    i = 0
    while i < len(values):
        # Walk a run of tied values, then assign each the average rank.
        j = i
        while j + 1 < len(values) and values[indexed[j + 1]] == values[indexed[i]]:
            j += 1
        avg = (i + j) / 2.0 + 1.0   # +1 for 1-based, avg of [i+1, j+1]
        for k_idx in range(i, j + 1):
            ranks[indexed[k_idx]] = avg
        i = j + 1
    return ranks
