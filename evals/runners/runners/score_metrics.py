"""Score-side runner helpers.

Field-level accuracy lives in ``run.py``/``compare.py``; this module
covers the *validator-level* metrics the P4 doc adds:

  - **Conflict precision/recall per kind** (task 15). Loads
    ``plantedConflicts`` from each ``golden.json`` and pairs it with
    the system's emitted Issues to build :class:`PacketResult` rows
    that :func:`conflict_metrics.measure` consumes.

  - **Tier agreement** (task 17). Loads ``human_tiers.json`` (with the
    mtime gate that protects against anchoring on the system's tier)
    and pairs it with the system's score-side output to build
    :class:`agreement.ScoreResult` rows that :func:`agreement.measure`
    consumes.

  - **Baseline payload assembly**. Emits the ``conflicts`` and
    ``agreement`` keys :file:`baseline.json` is supposed to carry once
    the regression gate guards them in earnest.

The actual HTTP plumbing (POST /api/providers/{id}/scores, polling
extraction status, etc.) lives in the orchestrator that task 18 wires —
this module is pure data shaping over already-fetched payloads, so it
unit-tests without a live API.

Wire-shape contracts:

  - Each ``planted`` dict carries at least ``kind`` / ``field`` /
    ``sources`` / ``expected_to_flag``. Optional ``shape`` (name_variant
    only) rides along untouched.
  - Each ``issue`` dict mirrors :class:`Domain.Scoring.Issue`'s JSON
    shape: ``validator``, ``severity``, ``field``, ``citations`` (each
    citation carries ``documentId`` / ``page`` / ``bbox`` /
    ``lowConfidence``). The metric only reads ``validator`` /
    ``field`` / ``citations``; the rest rides along.
  - ``score_response`` is a :class:`Domain.Scoring.ReadinessScoreDto`
    JSON shape: ``score`` (int 0–100), ``tier`` (``"Red"`` |
    ``"Yellow"`` | ``"Green"``), ``issues`` (list).

Wire-shape failures (missing keys, unrecognized tier) raise loudly via
:class:`ScoreEvalContractError` rather than coercing — silent coercion
is the failure mode that produced the P3 contract-error class in
``extractor_client.py``.
"""

from __future__ import annotations

import json
from collections.abc import Iterable, Mapping
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, cast

from runners.agreement import (
    AgreementMetrics,
    ScoreResult,
    TIER_TO_ORD,
    TierLabel,
    baseline_payload as _agreement_payload,
)
from runners.agreement import measure as agreement_measure
from runners.agreement import has_quorum as _has_quorum
from runners.conflict_metrics import (
    ConflictCount,
    PacketResult,
    baseline_payload as _conflicts_payload,
)
from runners.conflict_metrics import measure as conflict_measure


class ScoreEvalContractError(RuntimeError):
    """A score-side input doesn't match the locked wire contract.

    Distinct from :class:`extractor_client.ExtractorContractError` —
    the score eval consumes a different endpoint, so the failure
    attribution stays separate."""


class LabelsMtimeError(RuntimeError):
    """``human_tiers.json`` was modified after the corresponding
    ``baseline.json`` was generated. The agreement runner refuses to
    compute κ on labels that may have been anchored on system output.
    Re-label without seeing scores, or regenerate baseline first."""


# ---------------------------------------------------------------------------
# Planted-conflict loading
# ---------------------------------------------------------------------------


def load_planted_conflicts(golden_path: Path) -> tuple[dict[str, Any], ...]:
    """Read ``plantedConflicts`` from a packet's golden.json.

    Returns an empty tuple when the key is absent (clean-bucket
    packets). Raises :class:`ScoreEvalContractError` when a planted
    entry is missing required keys — a malformed planter would
    silently miss every recall denominator otherwise.
    """
    raw = json.loads(golden_path.read_text(encoding="utf-8"))
    planted = raw.get("plantedConflicts", [])
    if not isinstance(planted, list):
        raise ScoreEvalContractError(
            f"{golden_path}: plantedConflicts must be a list, got {type(planted).__name__}"
        )
    out: list[dict[str, Any]] = []
    for i, entry in enumerate(planted):
        if not isinstance(entry, dict):
            raise ScoreEvalContractError(
                f"{golden_path}: plantedConflicts[{i}] must be an object, got {type(entry).__name__}"
            )
        for required in ("kind", "field", "sources", "expected_to_flag"):
            if required not in entry:
                raise ScoreEvalContractError(
                    f"{golden_path}: plantedConflicts[{i}] missing required key {required!r}"
                )
        out.append(entry)
    return tuple(out)


# ---------------------------------------------------------------------------
# PacketResult / ScoreResult shaping
# ---------------------------------------------------------------------------


def build_packet_result(
    *,
    packet_id: str,
    planted_conflicts: Iterable[dict[str, Any]],
    score_response: Mapping[str, Any],
    doc_type_by_doc_id: Mapping[str, str],
) -> PacketResult:
    """Pair planted markers + emitted Issues into the dataclass
    :func:`conflict_metrics.measure` consumes.

    ``doc_type_by_doc_id`` is the per-packet documentId → docType map
    the orchestrator builds at upload time (POST /api/providers/{id}/documents
    returns a documentId; the orchestrator keeps the docType the upload
    was tagged with). Empty dict is legal but defeats predicate-2
    (sources overlap), so the caller should populate it whenever
    possible.
    """
    issues = score_response.get("issues")
    if not isinstance(issues, list):
        raise ScoreEvalContractError(
            f"score response for packet {packet_id!r} is missing `issues` list "
            f"(got {type(issues).__name__})"
        )
    return PacketResult(
        packet_id=packet_id,
        planted_conflicts=tuple(planted_conflicts),
        emitted_issues=tuple(issues),
        doc_type_by_doc_id=dict(doc_type_by_doc_id),
    )


def build_score_result(
    *,
    packet_id: str,
    score_response: Mapping[str, Any],
) -> ScoreResult:
    """Project a /api/providers/{id}/scores response to the agreement
    runner's input shape.

    Validates score/tier shape at the boundary so a drifted endpoint
    fails fast with the offending packet id, not later inside
    :func:`agreement.measure`.
    """
    score = score_response.get("score")
    tier = score_response.get("tier")
    if not isinstance(score, int):
        raise ScoreEvalContractError(
            f"score response for packet {packet_id!r}: `score` must be int, "
            f"got {type(score).__name__}"
        )
    if not isinstance(tier, str) or tier not in TIER_TO_ORD:
        raise ScoreEvalContractError(
            f"score response for packet {packet_id!r}: `tier` must be one of "
            f"{sorted(TIER_TO_ORD)}, got {tier!r}"
        )
    # cast: `tier in TIER_TO_ORD` above narrows the value to TierLabel.
    return ScoreResult(packet_id=packet_id, score=score, tier=cast("TierLabel", tier))


# ---------------------------------------------------------------------------
# Human labels + mtime gate
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class HumanTiers:
    """In-memory view of ``human_tiers.json``.

    ``labels`` values are narrowed to :data:`agreement.TierLabel` at load
    time, so downstream consumers can pass the map straight to
    :func:`agreement.measure` without re-validating the vocabulary.
    """
    labels: Mapping[str, TierLabel]
    # The file's mtime as the labeler last saved it. The gate compares
    # this against the baseline's generatedAt.
    written_at: datetime
    # The ``_biasNote`` field carried in-band so the README / audit can
    # surface it without a second read. Empty string if absent.
    bias_note: str


def load_human_tiers(
    labels_path: Path,
    *,
    baseline_generated_at: datetime | None = None,
) -> HumanTiers:
    """Read ``human_tiers.json`` and apply the mtime anchoring gate.

    The agreement runner refuses to compute κ when the labels file was
    written *after* the corresponding baseline.json was generated —
    anchoring discipline. Pass ``baseline_generated_at=None`` to skip
    the gate (smoke runs, dev iteration on the loader itself).

    Returns:
        :class:`HumanTiers` carrying the parsed map + provenance.

    Raises:
        FileNotFoundError: labels file doesn't exist.
        ScoreEvalContractError: the JSON shape is invalid or a tier
            value is outside the locked vocabulary.
        LabelsMtimeError: gate triggered.
    """
    if not labels_path.exists():
        raise FileNotFoundError(
            f"human_tiers.json not found at {labels_path}. "
            "Task 16: hand-label 20 packets before running agreement."
        )

    raw = json.loads(labels_path.read_text(encoding="utf-8"))
    if not isinstance(raw, dict):
        raise ScoreEvalContractError(
            f"{labels_path}: top-level must be an object, got {type(raw).__name__}"
        )
    labels_block = raw.get("labels")
    if not isinstance(labels_block, dict) or not labels_block:
        raise ScoreEvalContractError(
            f"{labels_path}: `labels` must be a non-empty object"
        )
    # Tier-vocabulary check up front so a typo is named here rather
    # than deep inside agreement.measure where the source packet id is
    # paired with a less-helpful message.
    for pid, tier in labels_block.items():
        if tier not in TIER_TO_ORD:
            raise ScoreEvalContractError(
                f"{labels_path}: label for packet {pid!r} is {tier!r}; "
                f"must be one of {sorted(TIER_TO_ORD)}"
            )

    mtime = datetime.fromtimestamp(labels_path.stat().st_mtime, tz=UTC)
    if baseline_generated_at is not None and mtime > baseline_generated_at:
        raise LabelsMtimeError(
            f"{labels_path} was modified at {mtime.isoformat()}, after the "
            f"corresponding baseline.json was generated at "
            f"{baseline_generated_at.isoformat()}. The anchoring gate refuses "
            f"to compute κ on labels that may have been adjusted in response "
            f"to system output. Re-label without seeing scores, or regenerate "
            f"baseline first; see phase-4-scale-and-llm-validators.md task 17."
        )

    bias_note = raw.get("_biasNote", "")
    if not isinstance(bias_note, str):
        raise ScoreEvalContractError(
            f"{labels_path}: `_biasNote` must be a string, got {type(bias_note).__name__}"
        )
    return HumanTiers(
        # cast: every value passed the TIER_TO_ORD check above.
        labels=cast("dict[str, TierLabel]", dict(labels_block)),
        written_at=mtime,
        bias_note=bias_note,
    )


# ---------------------------------------------------------------------------
# Baseline payload assembly
# ---------------------------------------------------------------------------


def measure_all(
    packet_results: list[PacketResult],
    score_results: Mapping[str, ScoreResult],
    labels: Mapping[str, str] | None,
) -> tuple[dict[str, ConflictCount], AgreementMetrics | None]:
    """Run both metric pipelines in one call.

    Returns a (conflict_counts, agreement_metrics) pair. The agreement
    side is ``None`` when ``labels`` is None or the intersection with
    ``score_results`` is below :data:`agreement.QUORUM_FLOOR` — the gate
    at the runner surface should report the skip; this function just
    emits None so callers can branch cleanly.
    """
    counts = conflict_measure(packet_results)
    if labels is None or not _has_quorum(score_results, labels):
        return counts, None
    return counts, agreement_measure(score_results, labels)


def baseline_payload(
    counts: dict[str, ConflictCount],
    agreement: AgreementMetrics | None,
) -> dict[str, Any]:
    """Serialize the score-side metrics to the shape ``baseline.json``
    carries under the ``conflicts`` and ``agreement`` keys.

    The ``agreement`` key is omitted entirely when ``agreement`` is
    None — better-no-claim-than-wrong-one. A reader expecting it can
    fail explicitly on the missing key rather than silently consuming
    a zeroed-out stub.
    """
    out: dict[str, Any] = {"conflicts": _conflicts_payload(counts)}
    if agreement is not None:
        out["agreement"] = _agreement_payload(agreement)
    return out
