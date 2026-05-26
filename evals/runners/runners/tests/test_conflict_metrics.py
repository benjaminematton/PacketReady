"""Unit coverage for the 3-predicate catch definition in conflict_metrics.

Pins:
  - happy-path catch on name_variant + taxonomy_specialty_mismatch,
  - predicate-1 failure (wrong validator),
  - predicate-2 failure (citations don't name a planted source),
  - predicate-3 failure (right validator, wrong field),
  - fabrication: validator flags on a clean packet,
  - typo-tolerance shape (expected_to_flag=False) does NOT count toward
    recall; flagging it bumps the fabrication count via the
    expected-quiet-validator path.
  - one Issue can't catch two planted entries (claimed-once invariant).
"""

from __future__ import annotations

from runners.conflict_metrics import (
    EXPECTED_VALIDATOR,
    PacketResult,
    measure,
)


def _planted(
    kind: str,
    field: str,
    sources: list[str],
    *,
    expected_to_flag: bool = True,
) -> dict:
    return {
        "kind": kind,
        "field": field,
        "sources": sources,
        "expected_to_flag": expected_to_flag,
        "expectedSeverity": "Critical",
    }


def _issue(
    validator: str,
    field: str,
    citations_doc_types: list[str],
    doc_type_index: dict[str, str],
) -> dict:
    # Build citations whose documentId resolves to the desired docType via
    # the per-packet index. Real Issue payloads carry richer fields; the
    # metric only reads validator/field/citations.
    inv_index = {v: k for k, v in doc_type_index.items()}
    return {
        "validator": validator,
        "field": field,
        "citations": [{"documentId": inv_index[dt]} for dt in citations_doc_types],
    }


def _doc_index() -> dict[str, str]:
    # Stable per-test doc-id-to-docType map; the inverse is used by _issue.
    return {
        "doc-license":     "license",
        "doc-dea":         "dea",
        "doc-boardCert":   "boardCert",
        "doc-malpractice": "malpractice",
    }


def test_name_variant_catch_all_predicates_pass():
    idx = _doc_index()
    packet = PacketResult(
        packet_id="packet-x",
        planted_conflicts=(_planted(
            "name_variant", "malpractice.fullName", ["license", "malpractice"]),),
        emitted_issues=(
            _issue("identity_coherence", "malpractice.fullName",
                   ["license", "malpractice"], idx),
        ),
        doc_type_by_doc_id=idx,
    )

    counts = measure([packet])

    assert counts["name_variant"].planted == 1
    assert counts["name_variant"].caught == 1
    assert counts["name_variant"].fabricated == 0
    assert counts["name_variant"].precision == 1.0
    assert counts["name_variant"].recall == 1.0


def test_taxonomy_mismatch_catch():
    idx = _doc_index()
    packet = PacketResult(
        packet_id="packet-y",
        planted_conflicts=(_planted(
            "taxonomy_specialty_mismatch", "boardCert.specialty",
            ["license", "boardCert"]),),
        emitted_issues=(
            _issue("npi_taxonomy_match", "boardCert.specialty",
                   ["license", "boardCert"], idx),
        ),
        doc_type_by_doc_id=idx,
    )

    counts = measure([packet])

    assert counts["taxonomy_specialty_mismatch"].caught == 1
    assert counts["taxonomy_specialty_mismatch"].recall == 1.0


def test_predicate1_failure_wrong_validator():
    # Right field + sources, but the issue came from license_status, not
    # identity_coherence. Not a catch.
    idx = _doc_index()
    packet = PacketResult(
        packet_id="p1",
        planted_conflicts=(_planted(
            "name_variant", "malpractice.fullName", ["license", "malpractice"]),),
        emitted_issues=(
            _issue("license_status", "malpractice.fullName",
                   ["license", "malpractice"], idx),
        ),
        doc_type_by_doc_id=idx,
    )

    counts = measure([packet])

    assert counts["name_variant"].caught == 0
    assert counts["name_variant"].planted == 1


def test_predicate2_failure_sources_dont_overlap():
    # Right validator + field, but citations point at dea/boardCert and the
    # planted sources are license/malpractice. No overlap → no catch.
    idx = _doc_index()
    packet = PacketResult(
        packet_id="p2",
        planted_conflicts=(_planted(
            "name_variant", "malpractice.fullName", ["license", "malpractice"]),),
        emitted_issues=(
            _issue("identity_coherence", "malpractice.fullName",
                   ["dea", "boardCert"], idx),
        ),
        doc_type_by_doc_id=idx,
    )

    counts = measure([packet])

    assert counts["name_variant"].caught == 0


def test_predicate3_failure_right_validator_wrong_field():
    # The off-target finding the 3rd predicate exists to catch: identity_coherence
    # noticed a different drift (DOB) on the planted name_variant pair.
    idx = _doc_index()
    packet = PacketResult(
        packet_id="p3",
        planted_conflicts=(_planted(
            "name_variant", "malpractice.fullName", ["license", "malpractice"]),),
        emitted_issues=(
            _issue("identity_coherence", "malpractice.dateOfBirth",
                   ["license", "malpractice"], idx),
        ),
        doc_type_by_doc_id=idx,
    )

    counts = measure([packet])

    assert counts["name_variant"].caught == 0
    # The off-target issue counts as a fabrication on a packet where the
    # validator was supposed to stay silent on dob drift — but the
    # planted name_variant IS expected_to_flag, so identity_coherence is
    # in the "expected-flag" set, not the quiet set. No fabrication.
    assert counts["name_variant"].fabricated == 0


def test_fabrication_on_clean_packet():
    # No planted entries → any identity_coherence Issue is a fabrication.
    idx = _doc_index()
    packet = PacketResult(
        packet_id="clean",
        planted_conflicts=(),
        emitted_issues=(
            _issue("identity_coherence", "malpractice.fullName",
                   ["license", "malpractice"], idx),
        ),
        doc_type_by_doc_id=idx,
    )

    counts = measure([packet])

    assert counts["name_variant"].planted == 0
    assert counts["name_variant"].caught == 0
    assert counts["name_variant"].fabricated == 1
    assert counts["name_variant"].precision == 0.0
    assert counts["name_variant"].recall is None  # no planted to divide by


def test_typo_tolerance_shape_not_in_recall_denominator():
    # Planted entry with expected_to_flag=False — the validator should
    # stay silent. If it flags, that's a fabrication. The planted entry
    # does NOT add to the recall denominator.
    idx = _doc_index()
    packet = PacketResult(
        packet_id="typo",
        planted_conflicts=(_planted(
            "name_variant", "malpractice.fullName", ["license", "malpractice"],
            expected_to_flag=False),),
        emitted_issues=(
            _issue("identity_coherence", "malpractice.fullName",
                   ["license", "malpractice"], idx),
        ),
        doc_type_by_doc_id=idx,
    )

    counts = measure([packet])

    assert counts["name_variant"].planted == 0  # tolerance shape excluded
    assert counts["name_variant"].caught == 0
    assert counts["name_variant"].fabricated == 1


def test_one_issue_cannot_catch_two_planted_entries():
    # Two planted name_variant entries in the same packet (degenerate but
    # legal); only one issue. Recall denominator is 2, caught is 1.
    idx = _doc_index()
    packet = PacketResult(
        packet_id="dup",
        planted_conflicts=(
            _planted("name_variant", "malpractice.fullName", ["license", "malpractice"]),
            _planted("name_variant", "malpractice.fullName", ["license", "malpractice"]),
        ),
        emitted_issues=(
            _issue("identity_coherence", "malpractice.fullName",
                   ["license", "malpractice"], idx),
        ),
        doc_type_by_doc_id=idx,
    )

    counts = measure([packet])

    assert counts["name_variant"].planted == 2
    assert counts["name_variant"].caught == 1


def test_expected_validator_map_locks_kinds_for_p4():
    # Lock-in: the two LLM validators are the only kinds measured in P4.
    # expiry_mismatch lands in Phase 4.5 with its own validator.
    assert set(EXPECTED_VALIDATOR.keys()) == {
        "name_variant", "taxonomy_specialty_mismatch",
    }
