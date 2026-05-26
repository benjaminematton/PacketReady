"""Per-kind conflict precision/recall on the eval dataset.

A planted conflict is "caught" iff ALL THREE predicates hold against at
least one of the system's emitted Issues:

  1. **Validator match.** The Issue's ``validator`` equals
     :data:`EXPECTED_VALIDATOR[kind]`.
  2. **Sources overlap.** The Issue's citations name at least one of the
     planted ``sources`` (e.g. ``["license", "malpractice"]`` for a
     ``name_variant``). Citation source comes from the citation's
     ``documentId`` resolving to a docType in our index, OR from the
     ``sourceValidator`` echoing the docType label (LLM validators today
     don't echo it; the doc-id path is the load-bearing one).
  3. **Field match.** The Issue's ``field`` discriminator (set by P4 LLM
     validators) equals the planted ``field``. This prevents a "right
     validator, wrong finding" — e.g. ``identity_coherence`` noticing a
     DOB drift when we planted a name_variant — from counting.

Per-kind precision = caught_and_planted / all_flagged_conflicts;
recall = caught_and_planted / total_planted (restricted to planted
entries flagged ``expected_to_flag=True``; the typo-tolerance shape
counts as a fabrication if any expected validator flags it).

Fabrications are counted on packets with no planted entry, AND on
packets whose planted entries are all ``expected_to_flag=False`` (the
validator was supposed to stay silent on that shape).

``expiry_mismatch`` is NOT in :data:`EXPECTED_VALIDATOR` — see the P4
doc's "Conflict kinds in P4" decision; that kind lands in a Phase 4.5
follow-on.
"""

from __future__ import annotations

from collections import defaultdict
from collections.abc import Iterable
from dataclasses import dataclass, field
from typing import Any

# Validator name → planted-kind map. Mirrors the C# IValidator.Name
# constants (see apps/api/Application/Scoring/Validators/*.cs).
EXPECTED_VALIDATOR: dict[str, str] = {
    "name_variant":                "identity_coherence",
    "taxonomy_specialty_mismatch": "npi_taxonomy_match",
}


@dataclass(frozen=True)
class PacketResult:
    """A single packet's planted markers paired with the system's emitted
    Issues. The runner builds this from ``golden.json`` (planted) +
    POST /api/scores/recompute response (issues).
    """
    packet_id: str
    # Each dict carries at minimum: kind, field, sources (list[str]),
    # expected_to_flag (bool). Optional shape (str, name_variant only).
    planted_conflicts: tuple[dict[str, Any], ...]
    # Each dict carries at minimum: validator (str), field (str),
    # citations (list[{documentId?, sourceValidator}]). The doc-id-to-
    # docType resolution is handled via doc_type_by_doc_id below.
    emitted_issues: tuple[dict[str, Any], ...]
    # documentId → docType label (e.g. "license"). The runner builds this
    # from the document upload response or the score-detail payload.
    # Empty dict is OK — the source-overlap predicate then falls back to
    # the Issue's sourceValidator field (which LLM validators don't echo,
    # so empty-dict callers will tend toward predicate-2 misses).
    doc_type_by_doc_id: dict[str, str] = field(default_factory=dict)


@dataclass
class ConflictCount:
    """Per-kind precision/recall tally. Mutable so the rollup can fold
    into it without re-materializing on every increment."""
    kind: str
    planted: int = 0
    caught: int = 0
    fabricated: int = 0

    @property
    def precision(self) -> float | None:
        flagged = self.caught + self.fabricated
        if flagged == 0:
            return None
        return round(self.caught / flagged, 4)

    @property
    def recall(self) -> float | None:
        if self.planted == 0:
            return None
        return round(self.caught / self.planted, 4)


def _citation_docs(
    citations: Iterable[dict[str, Any]],
    doc_type_by_doc_id: dict[str, str],
) -> set[str]:
    """Resolve the set of docType labels a list of citations covers.

    Tries documentId → docType first; falls back to a "<docType>." prefix
    on sourceValidator if the validator echoes it (P5+ possibility — none
    do today). Unresolved citations are silently dropped from the set.
    """
    out: set[str] = set()
    for c in citations or ():
        doc_id = c.get("documentId")
        if doc_id and doc_id in doc_type_by_doc_id:
            out.add(doc_type_by_doc_id[doc_id])
    return out


def _issue_catches(
    issue: dict[str, Any],
    planted: dict[str, Any],
    doc_type_by_doc_id: dict[str, str],
) -> bool:
    """All three predicates against a single issue/planted pair."""
    expected = EXPECTED_VALIDATOR.get(planted["kind"])
    if expected is None:
        return False

    # 1. Validator name match.
    if issue.get("validator") != expected:
        return False

    # 2. Sources overlap. Planted side names docType labels; the issue
    #    side resolves documentId → docType via the index.
    planted_sources = set(planted.get("sources", []))
    issue_sources = _citation_docs(issue.get("citations", []), doc_type_by_doc_id)
    if not (planted_sources & issue_sources):
        return False

    # 3. Field discriminator match. Pure-code validators leave the field
    #    blank; LLM validators set it. Comparing identity-on-empty-strings
    #    would pass two unrelated null findings, so require non-empty.
    planted_field = planted.get("field", "")
    issue_field = issue.get("field", "")
    if not planted_field or planted_field != issue_field:
        return False

    return True


def _expected_validators_for(packet: PacketResult) -> set[str]:
    """Validators that *should* have stayed silent on this packet — i.e.
    no must-catch planted entry for that validator's expected kind.

    Used to count fabrications: an emitted issue from an expected-quiet
    validator is a fabrication, even on a packet that does have other
    planted entries (different kind, or same kind but
    expected_to_flag=False).
    """
    must_flag_kinds = {
        p["kind"] for p in packet.planted_conflicts
        if p.get("expected_to_flag", True)
    }
    return {EXPECTED_VALIDATOR[k] for k in must_flag_kinds if k in EXPECTED_VALIDATOR}


def measure(packet_results: list[PacketResult]) -> dict[str, ConflictCount]:
    """Fold per-kind precision/recall over a flat list of packet results.

    Returns a dict keyed by planted kind. Kinds with zero observations
    still appear in the result with ``planted=caught=fabricated=0`` —
    downstream consumers can rely on the EXPECTED_VALIDATOR keyset.
    """
    counts: dict[str, ConflictCount] = {
        kind: ConflictCount(kind=kind) for kind in EXPECTED_VALIDATOR
    }

    # Per-packet flagged-issue accounting: track which Issues have been
    # claimed by a planted catch so we don't double-count one Issue
    # against two planted entries of different kinds in the same packet.
    for packet in packet_results:
        claimed_issue_ids: set[int] = set()

        for planted in packet.planted_conflicts:
            kind = planted["kind"]
            if kind not in counts:
                # Unknown / out-of-scope kind — skip. expiry_mismatch lands here.
                continue
            if not planted.get("expected_to_flag", True):
                # FP-tolerance shape: not in the recall denominator. The
                # validator emitting on this packet would be counted as a
                # fabrication below via the expected-quiet path.
                continue

            counts[kind].planted += 1
            for idx, issue in enumerate(packet.emitted_issues):
                if idx in claimed_issue_ids:
                    continue
                if _issue_catches(issue, planted, packet.doc_type_by_doc_id):
                    counts[kind].caught += 1
                    claimed_issue_ids.add(idx)
                    break

        # Fabrication count: any issue from an expected-quiet validator
        # that wasn't claimed by a real catch above. Validators we don't
        # measure (license_status, dea_status, etc.) are ignored.
        quiet_validators = {
            v for v in EXPECTED_VALIDATOR.values()
        } - _expected_validators_for(packet)
        for idx, issue in enumerate(packet.emitted_issues):
            if idx in claimed_issue_ids:
                continue
            validator_name = issue.get("validator")
            if validator_name in quiet_validators:
                # Back-map validator → kind for the right bucket.
                kind = next(
                    (k for k, v in EXPECTED_VALIDATOR.items() if v == validator_name),
                    None,
                )
                if kind is not None:
                    counts[kind].fabricated += 1

    return counts


def baseline_payload(counts: dict[str, ConflictCount]) -> dict[str, Any]:
    """Serialize per-kind counts into the shape :file:`baseline.json`
    carries under the ``conflicts`` key. Stable key ordering matches
    :data:`EXPECTED_VALIDATOR.keys()` so diffs stay readable across
    runs."""
    return {
        kind: {
            "planted":    counts[kind].planted,
            "caught":     counts[kind].caught,
            "fabricated": counts[kind].fabricated,
            "precision":  counts[kind].precision,
            "recall":     counts[kind].recall,
        }
        for kind in EXPECTED_VALIDATOR
    }
