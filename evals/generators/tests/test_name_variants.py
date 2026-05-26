"""
Pins the diversity contract: every CleanNamePattern and every ConflictShape
appears at least once across the 50-packet eval set, with expected counts.

If this fails: either the bucket plan or the rotation logic in
build_new_packets drifted, OR a new enum entry was added without rebalancing
the rotation. Fix the source of truth (rotation indexing in packets.py or
the enum), not the expected counts here.

Mathematical floor for each pattern/shape count assumes the rotation strategy
in packets.py (`packet_idx % len(CleanNamePattern)` for clean, separate
`name_conflict_idx % len(ConflictShape)` for conflict). With 45 new packets
and 5 clean patterns, each pattern appears 9 times. With 10 name-conflict
packets and 5 conflict shapes, each shape appears 2 times.
"""

from __future__ import annotations

from collections import Counter

from packetready_eval.name_variants import (
    EXPECTED_TO_FLAG,
    CleanNamePattern,
    ConflictShape,
    malpractice_variant_for_shape,
    names_for_clean_pattern,
)
from packetready_eval.packets import build_new_packets
from random import Random


def _planted_shape(spec) -> str | None:
    for m in spec.planted_conflicts:
        if m["kind"] == "name_variant":
            return m["shape"]
    return None


def _clean_pattern(spec) -> str:
    # Pattern is recorded in the notes block by `_spec_from_profile`.
    # Surface the substring rather than re-deriving from the per-doc names —
    # the latter would test names_for_clean_pattern via its own output.
    for token in spec.notes.split():
        if token.startswith("clean_pattern="):
            return token.split("=", 1)[1].rstrip(",.)")
    raise AssertionError(f"no clean_pattern in spec.notes for {spec.id}")


def test_every_clean_pattern_appears_across_new_packets() -> None:
    counts = Counter(_clean_pattern(s) for s in build_new_packets())
    assert set(counts) == {p.name for p in CleanNamePattern}, (
        f"missing patterns: {[p.name for p in CleanNamePattern if p.name not in counts]}"
    )
    # 45 packets / 5 patterns = 9 each.
    for pattern, count in counts.items():
        assert count == 9, f"{pattern}: expected 9 packets, got {count}"


def test_every_conflict_shape_appears_across_name_conflict_packets() -> None:
    shapes = [
        _planted_shape(s)
        for s in build_new_packets()
        if _planted_shape(s) is not None
    ]
    counts = Counter(shapes)
    assert set(counts) == {s.name for s in ConflictShape}, (
        f"missing shapes: {[s.name for s in ConflictShape if s.name not in counts]}"
    )
    # 10 name-conflict packets (7 clean+conflict-name + 3 scanned+conflict-name)
    # / 5 shapes = 2 each.
    for shape, count in counts.items():
        assert count == 2, f"{shape}: expected 2 packets, got {count}"


def test_expected_to_flag_consistent_with_enum_map() -> None:
    """Every planted name_variant marker's expected_to_flag matches
    EXPECTED_TO_FLAG[shape]. Catches the case where someone adds a shape
    but forgets to extend the map."""
    for spec in build_new_packets():
        for m in spec.planted_conflicts:
            if m["kind"] != "name_variant":
                continue
            shape = ConflictShape[m["shape"]]
            assert m["expected_to_flag"] is EXPECTED_TO_FLAG[shape], (
                f"{spec.id}: shape {m['shape']} expected_to_flag={m['expected_to_flag']} "
                f"but EXPECTED_TO_FLAG[{shape}]={EXPECTED_TO_FLAG[shape]}"
            )


def test_taxonomy_markers_always_expected_to_flag() -> None:
    """No taxonomy-typo equivalent in P4 — every planted taxonomy mismatch
    should be flagged."""
    for spec in build_new_packets():
        for m in spec.planted_conflicts:
            if m["kind"] == "taxonomy_specialty_mismatch":
                assert m["expected_to_flag"] is True, f"{spec.id}: {m}"


# ---- name_variants module-level coverage --------------------------------


def test_names_for_clean_pattern_distinguishes_credentials() -> None:
    md = names_for_clean_pattern(CleanNamePattern.CREDENTIAL_MD, "Henry", "Anderson")
    md_periods = names_for_clean_pattern(CleanNamePattern.CREDENTIAL_PERIODS, "Henry", "Anderson")
    assert md.license.endswith(", MD")
    assert md_periods.license.endswith(", M.D.")
    # DEA omits the credential in both cases.
    assert "MD" not in md.dea and "M.D." not in md.dea


def test_malpractice_variant_surname_typo_preserves_first_name() -> None:
    """SURNAME_TYPO mutates only the surname; first name and credential
    must round-trip exactly so the FP-discipline rule scopes cleanly."""
    variant = malpractice_variant_for_shape(
        ConflictShape.SURNAME_TYPO, "Henry", "Anderson", Random(0), license_suffix=", MD",
    )
    assert variant.startswith("Henry ")
    assert variant.endswith(", MD")
    # The surname changed by one letter (length-preserving).
    new_surname = variant.removeprefix("Henry ").removesuffix(", MD")
    assert len(new_surname) == len("Anderson")
    assert new_surname != "Anderson"


def test_malpractice_variant_surname_swap_changes_surname() -> None:
    variant = malpractice_variant_for_shape(
        ConflictShape.SURNAME_SWAP, "Henry", "Anderson", Random(0), license_suffix=", MD",
    )
    assert "Anderson" not in variant
    assert variant.startswith("Henry ")
    assert variant.endswith(", MD")


def test_malpractice_variant_deterministic_per_seed() -> None:
    for shape in ConflictShape:
        a = malpractice_variant_for_shape(shape, "Henry", "Anderson", Random(42))
        b = malpractice_variant_for_shape(shape, "Henry", "Anderson", Random(42))
        assert a == b, f"{shape}: not deterministic across same-seed calls"
