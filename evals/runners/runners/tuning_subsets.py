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

import json
from pathlib import Path
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

# build_new_packets() runs the full Faker + NPPES sampling pipeline — cache
# its result at module load so heldout/tuning derivation + variant coverage
# don't re-pay it on every reference.
_ALL_SPECS = (*PACKET_SPECS, *build_new_packets())
_ALL_PACKET_IDS: tuple[str, ...] = tuple(sorted(s.id for s in _ALL_SPECS))

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
    specs_by_id = {s.id: s for s in _ALL_SPECS}
    tuning_specs = [specs_by_id[i] for i in IDENTITY_COHERENCE_TUNING]

    clean_patterns_present = {
        spec.clean_pattern.name for spec in tuning_specs if spec.clean_pattern is not None
    }
    conflict_shapes_present = {
        marker["shape"]
        for spec in tuning_specs
        for marker in spec.planted_conflicts
        if marker.get("kind") == "name_variant" and "shape" in marker
    }

    missing_patterns = {p.name for p in CleanNamePattern} - clean_patterns_present
    missing_shapes = {s.name for s in ConflictShape} - conflict_shapes_present
    assert not missing_patterns, (
        f"IDENTITY_COHERENCE_TUNING missing CleanNamePattern coverage: {missing_patterns}"
    )
    assert not missing_shapes, (
        f"IDENTITY_COHERENCE_TUNING missing ConflictShape coverage: {missing_shapes}"
    )


_variant_coverage_check()


# ---------------------------------------------------------------------------
# Shared manifest: this Python module is the source of truth; the C# tuning
# CLI reads `evals/tuning_subsets.json` at startup. Keeping the C# side as a
# read-from-disk consumer (rather than a duplicate hardcoded list) means any
# future re-pick of the tuning subset propagates without a manual edit on the
# .NET side.
# ---------------------------------------------------------------------------

MANIFEST_RELATIVE_PATH = Path("evals") / "tuning_subsets.json"


def manifest_dict() -> dict[str, list[str]]:
    """The on-disk JSON shape that both Python and C# consume."""
    return {
        "identityCoherenceTuning": list(IDENTITY_COHERENCE_TUNING),
        "identityCoherenceHeldout": list(IDENTITY_COHERENCE_HELDOUT),
    }


def write_manifest(repo_root: Path) -> Path:
    """Writes `evals/tuning_subsets.json` under `repo_root`. Called by the
    packet-regen CLI so artifacts stay in lockstep."""
    path = repo_root / MANIFEST_RELATIVE_PATH
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(manifest_dict(), indent=2) + "\n",
        encoding="utf-8",
    )
    return path


def _repo_root_from_here() -> Path:
    # tuning_subsets.py lives at <repo>/evals/runners/runners/.
    return Path(__file__).resolve().parents[3]


def _assert_on_disk_manifest_matches() -> None:
    """If the JSON manifest exists on disk, assert it agrees with what this
    module computed. Drift means someone added a packet without re-running
    `python -m packetready_eval.packets evals/dataset/` — fail loudly so the
    C# CLI doesn't run against a stale subset."""
    path = _repo_root_from_here() / MANIFEST_RELATIVE_PATH
    if not path.exists():
        return
    try:
        on_disk = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise AssertionError(f"{path} is not valid JSON: {exc}") from exc
    expected = manifest_dict()
    if on_disk != expected:
        raise AssertionError(
            f"On-disk {path.name} is out of sync with this module's tuples. "
            f"Re-run `python -m packetready_eval.packets evals/dataset/` "
            f"(or `python -m runners.tuning_subsets --write`) to refresh it.\n"
            f"  on-disk:  {on_disk}\n"
            f"  expected: {expected}"
        )


_assert_on_disk_manifest_matches()


def _main(argv: list[str] | None = None) -> int:
    import argparse
    parser = argparse.ArgumentParser(
        description="Manage the tuning_subsets.json manifest read by the C# tuning CLI."
    )
    parser.add_argument(
        "--write",
        action="store_true",
        help="Write the manifest under <repo>/evals/tuning_subsets.json.",
    )
    args = parser.parse_args(argv)
    if args.write:
        path = write_manifest(_repo_root_from_here())
        print(f"wrote {path}")
        return 0
    parser.print_help()
    return 0


if __name__ == "__main__":
    import sys
    sys.exit(_main())
