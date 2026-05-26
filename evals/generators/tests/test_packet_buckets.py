"""
Locks P4 DoD: `evals/dataset/` holds **50 packets** in 4 buckets:
  15 clean+valid · 15 clean+conflicts · 15 scanned+clean · 5 scanned+conflicts.

If this fails, either the bucket plan in `build_new_packets` drifted, or the
P2 grandfathered packets were edited in a way that changed which bucket they
fall into. Fix the source of truth (_BUCKET_PLAN or PACKET_SPECS), not the
expected counts here.
"""

from __future__ import annotations

from collections import Counter

from packetready_eval.packets import PACKET_SPECS, build_new_packets


def _bucket(spec) -> str:
    conflicted = bool(spec.planted_conflicts)
    return f"{'scanned' if spec.scanned else 'clean'}+{'conflicts' if conflicted else 'valid'}"


def test_total_count_is_50() -> None:
    assert len(PACKET_SPECS) + len(build_new_packets()) == 50


def test_bucket_counts_match_dod() -> None:
    counts = Counter(_bucket(s) for s in PACKET_SPECS + build_new_packets())
    assert counts == {
        "clean+valid":      15,
        "clean+conflicts":  15,
        "scanned+valid":    15,   # "scanned+clean" in doc prose; "valid" = no conflicts
        "scanned+conflicts": 5,
    }


def test_all_packet_ids_unique() -> None:
    all_ids = [s.id for s in PACKET_SPECS + build_new_packets()]
    dups = [k for k, v in Counter(all_ids).items() if v > 1]
    assert not dups, f"duplicate packet ids: {dups}"


def test_new_packets_are_deterministic() -> None:
    # Two builds must produce byte-identical specs — the regression gate's
    # baseline.json is only meaningful if the generator is reproducible.
    a = build_new_packets()
    b = build_new_packets()
    assert a == b


def test_p4_planted_kinds_only() -> None:
    # Among NEW packets only — P2's packet-004 carries an expiry_mismatch
    # grandfather that P4 deliberately ignores (lands in P4.5).
    permitted = {"name_variant", "taxonomy_specialty_mismatch"}
    for spec in build_new_packets():
        for marker in spec.planted_conflicts:
            assert marker["kind"] in permitted, (
                f"{spec.id} plants {marker['kind']!r} — not a P4-measured kind"
            )
