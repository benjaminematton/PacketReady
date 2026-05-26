"""
Curated 10-packet subsets used for per-LLM-validator prompt tuning, plus a
held-out 10 used ONLY for honest out-of-sample validation.

The full 50-packet eval costs ~$1.20/run for IdentityCoherence alone (each
packet hits Sonnet once), so tuning a prompt on the full set is the wrong
fast loop — each iteration would burn ~$1.20 just to measure. Each LLM
validator gets a 10-packet subset deliberately covering the FP/recall axes
its prompt has to learn:

  - IDENTITY_COHERENCE_TUNING: one packet per CleanNamePattern (5) + one
    per ConflictShape (5). Tuning iterations measure against this 10. The
    --packets flag on tools/TuneIdentityCoherence defaults to this tuple.

  - IDENTITY_COHERENCE_HELDOUT: random 10 drawn from all 50 with seed=9999.
    Disjoint from the tuning subset. NOT used during tuning iterations —
    the CLI refuses to run on these IDs unless --allow-heldout is passed.
    Final-validation runs use --allow-heldout explicitly; the worst-case
    metrics across 3 runs on this subset are what go in the README headline.

This separation matters because "validated on the same 50 we tuned against"
isn't a generalization claim. With held-out kept disjoint, the held-out
number is genuinely out-of-sample.

P4 task 9 tunes :pyattr:`IDENTITY_COHERENCE_TUNING`. P4 task 10 will add
``NPI_TAXONOMY_MATCH_TUNING`` + ``NPI_TAXONOMY_MATCH_HELDOUT`` when that
validator lands; the same diversity + disjointness assertions apply.
"""

from __future__ import annotations

from random import Random

# Importing from packetready_eval pins the subset definitions to the canonical
# generator output — adding a packet to PACKET_SPECS or build_new_packets
# changes the held-out draw automatically, and the variant-coverage assertions
# fail loudly if rotation drift means a CleanNamePattern or ConflictShape
# stops appearing.
from packetready_eval.name_variants import CleanNamePattern, ConflictShape
from packetready_eval.packets import PACKET_SPECS, build_new_packets


# ---------------------------------------------------------------------------
# Held-out 10 — seed-pinned random draw across all 50 packets.
# ---------------------------------------------------------------------------

_HELDOUT_SEED: int = 9999
_HELDOUT_SIZE: int = 10


def _all_packet_ids() -> tuple[str, ...]:
    """All packet ids across the P2 hand-crafted PACKET_SPECS and the
    programmatic build_new_packets(), sorted for stable ordering."""
    return tuple(sorted(s.id for s in [*PACKET_SPECS, *build_new_packets()]))


_ALL_PACKET_IDS: tuple[str, ...] = _all_packet_ids()

IDENTITY_COHERENCE_HELDOUT: tuple[str, ...] = tuple(sorted(
    Random(_HELDOUT_SEED).sample(_ALL_PACKET_IDS, _HELDOUT_SIZE)
))
"""10 packet ids reserved for out-of-sample validation only. Tuning iterations
must NOT run against these (CLI enforces). Final-validation runs cite this
subset's worst-case metrics in the README. Re-seeding _HELDOUT_SEED would
invalidate the held-out claim — bumping it requires a corresponding README
note and a fresh validation run."""


# ---------------------------------------------------------------------------
# Tuning subset — one packet per CleanNamePattern + one per ConflictShape.
# Hand-derived from the post-heldout candidate pool so the IDs are scannable,
# but the variant-coverage assertions below verify the picks against the
# canonical generator state at import time.
# ---------------------------------------------------------------------------

IDENTITY_COHERENCE_TUNING: tuple[str, ...] = (
    # Clean+valid (5) — one per CleanNamePattern. The validator must emit
    # zero disagreements; each packet exercises a different normalization
    # case the prompt has to learn to ignore.
    "packet-010-clean-berry",                       # CREDENTIAL_MD
    "packet-006-clean-perry",                       # CREDENTIAL_PERIODS
    "packet-018-clean-rogers",                      # HYPHENATED_ALREADY
    "packet-007-clean-myers",                       # MIDDLE_INITIAL
    "packet-014-clean-barker",                      # WHITESPACE_VARIANT
    # Conflict (5) — one per ConflictShape. SURNAME_TYPO is the
    # don't-flag conflict (expected_to_flag=False); the other four are
    # must-flag. The validator's per-shape outcomes are what the iteration
    # log and the category-counts aggregator track.
    "packet-003-conflict-name",                     # HYPHENATED_SUFFIX (P2 grandfather)
    "packet-025-clean-conflict-name-bartlett",      # MIDDLE_NAME_ADDED
    "packet-021-clean-conflict-name-guzman",        # NICKNAME
    "packet-022-clean-conflict-name-alexander",     # SURNAME_TYPO (do-not-flag)
    "packet-023-clean-conflict-name-cummings",      # SURNAME_SWAP
)

# ---------------------------------------------------------------------------
# Module-level assertions: fire at import if the subsets drift from the
# canonical generator. A failure here means the rotation in packets.py
# moved a pattern/shape off one of these IDs — either re-pick this subset
# or fix the rotation, don't suppress the assertion.
# ---------------------------------------------------------------------------

assert len(IDENTITY_COHERENCE_HELDOUT) == _HELDOUT_SIZE
assert len(set(IDENTITY_COHERENCE_HELDOUT)) == _HELDOUT_SIZE
assert set(IDENTITY_COHERENCE_HELDOUT).issubset(_ALL_PACKET_IDS), (
    "held-out subset references unknown packet ids — generator changed?"
)

assert len(IDENTITY_COHERENCE_TUNING) == 10
assert len(set(IDENTITY_COHERENCE_TUNING)) == 10
assert set(IDENTITY_COHERENCE_TUNING).issubset(_ALL_PACKET_IDS), (
    "tuning subset references unknown packet ids — generator changed?"
)
assert set(IDENTITY_COHERENCE_TUNING).isdisjoint(IDENTITY_COHERENCE_HELDOUT), (
    "tuning subset overlaps held-out — one of them must be re-picked; "
    "honest out-of-sample validation requires disjointness."
)


def _variant_coverage_check() -> None:
    """Asserts the tuning subset covers every CleanNamePattern + every
    ConflictShape. Run at import; raises AssertionError with a precise diff
    if a pattern/shape is missing. Re-pick the absent IDs from the canonical
    generator state and update the tuple above."""
    specs_by_id = {s.id: s for s in [*PACKET_SPECS, *build_new_packets()]}
    tuning_specs = [specs_by_id[i] for i in IDENTITY_COHERENCE_TUNING]

    clean_patterns_present = set()
    conflict_shapes_present = set()
    for spec in tuning_specs:
        for tok in spec.notes.split():
            if tok.startswith("clean_pattern="):
                clean_patterns_present.add(tok.split("=", 1)[1].rstrip(",.)"))
        for marker in spec.planted_conflicts:
            if marker.get("kind") == "name_variant" and "shape" in marker:
                conflict_shapes_present.add(marker["shape"])

    missing_patterns = {p.name for p in CleanNamePattern} - clean_patterns_present
    missing_shapes = {s.name for s in ConflictShape} - conflict_shapes_present
    assert not missing_patterns, (
        f"IDENTITY_COHERENCE_TUNING missing CleanNamePattern coverage: {missing_patterns}"
    )
    assert not missing_shapes, (
        f"IDENTITY_COHERENCE_TUNING missing ConflictShape coverage: {missing_shapes}"
    )


_variant_coverage_check()
