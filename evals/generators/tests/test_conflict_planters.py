"""
Locks the planter contract: mutation lands on the right doc(s), other docs
stay byte-identical, the plantedConflicts marker carries the runner-required
fields (kind / field / sources / expectedSeverity), and the same seed
produces the same mutation.
"""

from __future__ import annotations

from dataclasses import replace
from random import Random

import pytest

from packetready_eval.conflict_planters import (
    plant_name_variant,
    plant_taxonomy_specialty_mismatch,
)
from packetready_eval.docs.board_cert_pdf import BoardCertFields
from packetready_eval.docs.dea_pdf import DeaFields
from packetready_eval.docs.license_pdf import LicenseFields
from packetready_eval.docs.malpractice_pdf import MalpracticeFields
from packetready_eval.packets import PacketSpec
from packetready_eval.specialty_catalog import SPECIALTY_CATALOG

# Common marker keys across every planted conflict kind.
_COMMON_MARKER_KEYS = {
    "kind", "field", "sources", "description",
    "expectedSeverity", "expected_to_flag",
}
# name_variant additionally carries `shape` so conflict_metrics.py can break
# recall down per ConflictShape.
_NAME_VARIANT_MARKER_KEYS = _COMMON_MARKER_KEYS | {"shape"}
_TAXONOMY_MARKER_KEYS = _COMMON_MARKER_KEYS


def _clean_spec() -> PacketSpec:
    return PacketSpec(
        id="packet-test",
        label="Dr. Test",
        license_fields=LicenseFields(
            full_name="Test Person, MD",
            license_number="MD-NY-00001",
            state="NY",
            issue_date="2020-01-01",
            expiry_date="2027-01-01",
            status="Active",
            taxonomy_code="207R00000X",  # Internal Medicine
        ),
        dea_fields=DeaFields(
            full_name="Test Person",
            dea_number="BT0000001",
            expiry_date="2027-06-30",
            status="Active",
            schedules=("II", "III", "IV", "V"),
        ),
        board_cert_fields=BoardCertFields(
            full_name="Test Person, MD",
            board="ABIM",
            specialty="Internal Medicine",
            issue_date="2019-01-01",
            expiry_date="2029-01-01",
            status="Active",
        ),
        malpractice_fields=MalpracticeFields(
            full_name="Test Person, MD",
            carrier="TestCo",
            policy_number="TC-00001",
            expiry_date="2027-03-31",
            status="Active",
            licensee_license_number="MD-NY-00001",
            licensee_license_expiry="2027-01-01",
        ),
    )


# --- name_variant ----------------------------------------------------------

def test_plant_name_variant_mutates_malpractice_only() -> None:
    base = _clean_spec()
    planted = plant_name_variant(base, Random(0))
    assert planted.malpractice_fields.full_name != base.malpractice_fields.full_name
    assert planted.license_fields == base.license_fields
    assert planted.dea_fields == base.dea_fields
    assert planted.board_cert_fields == base.board_cert_fields


def test_plant_name_variant_marker_shape() -> None:
    planted = plant_name_variant(_clean_spec(), Random(0))
    assert len(planted.planted_conflicts) == 1
    marker = planted.planted_conflicts[0]
    assert marker.keys() == _NAME_VARIANT_MARKER_KEYS
    assert marker["kind"] == "name_variant"
    assert marker["field"] == "malpractice.fullName"
    assert marker["sources"] == ["license", "malpractice"]
    assert marker["expectedSeverity"] == "Critical"
    # Default shape stays HYPHENATED_SUFFIX for back-compat with pre-task-9
    # callers that didn't pass a `shape=` arg.
    assert marker["shape"] == "HYPHENATED_SUFFIX"
    assert marker["expected_to_flag"] is True


def test_plant_name_variant_variant_carries_hyphen_and_initial() -> None:
    planted = plant_name_variant(_clean_spec(), Random(0))
    variant = planted.malpractice_fields.full_name
    assert "-" in variant
    assert "." in variant       # middle initial
    assert variant.endswith(", MD")


def test_plant_name_variant_avoids_degenerate_self_pair() -> None:
    # A license surname that's also in the suffix pool must NOT produce a
    # "Smith-Smith" hyphenation — that's a generator bug, not a credible
    # name-variant conflict.
    base = replace(_clean_spec(), license_fields=replace(
        _clean_spec().license_fields, full_name="John Smith, MD"
    ))
    for seed in range(20):
        planted = plant_name_variant(base, Random(seed))
        assert "Smith-Smith" not in planted.malpractice_fields.full_name


def test_plant_name_variant_deterministic_for_same_seed() -> None:
    a = plant_name_variant(_clean_spec(), Random(7))
    b = plant_name_variant(_clean_spec(), Random(7))
    assert a == b


def test_plant_name_variant_appends_to_existing_markers() -> None:
    base = replace(
        _clean_spec(),
        planted_conflicts=[{"kind": "pre_existing", "field": "x", "sources": [],
                            "description": "", "expectedSeverity": "Minor"}],
    )
    planted = plant_name_variant(base, Random(0))
    assert len(planted.planted_conflicts) == 2
    assert planted.planted_conflicts[0]["kind"] == "pre_existing"
    assert planted.planted_conflicts[1]["kind"] == "name_variant"


def test_plant_name_variant_rejects_single_token_name() -> None:
    base = replace(_clean_spec(), license_fields=replace(
        _clean_spec().license_fields, full_name="Cher"
    ))
    with pytest.raises(ValueError, match="first and last name"):
        plant_name_variant(base, Random(0))


# --- taxonomy_specialty_mismatch ------------------------------------------

def test_plant_taxonomy_mismatch_mutates_license_and_board() -> None:
    base = _clean_spec()
    planted = plant_taxonomy_specialty_mismatch(base, Random(0))
    assert planted.license_fields.taxonomy_code != base.license_fields.taxonomy_code
    assert planted.board_cert_fields.specialty != base.board_cert_fields.specialty
    # Board acronym tracks specialty — the board cert PDF stays internally
    # consistent; the only cross-doc disagreement is license-vs-board on specialty.
    assert planted.board_cert_fields.board != base.board_cert_fields.board
    assert planted.dea_fields == base.dea_fields
    assert planted.malpractice_fields == base.malpractice_fields


def test_plant_taxonomy_mismatch_picks_distinct_specialties() -> None:
    # Assert against the structured fields the planter mutated, not the prose
    # description — wording tweaks to `description` can't ever break this.
    code_to_specialty = {
        info.taxonomy_code: name
        for name, info in SPECIALTY_CATALOG.items()
        if info.taxonomy_code
    }
    for seed in range(20):
        planted = plant_taxonomy_specialty_mismatch(_clean_spec(), Random(seed))
        license_specialty = code_to_specialty[planted.license_fields.taxonomy_code]
        board_specialty = planted.board_cert_fields.specialty
        assert license_specialty != board_specialty, (
            f"seed {seed}: identical specialties {license_specialty!r}"
        )


def test_plant_taxonomy_mismatch_board_acronym_matches_new_specialty() -> None:
    # The board cert PDF must remain internally consistent: its `board` field
    # must be the body that actually certifies its `specialty`. Otherwise
    # tuning a "board ↔ specialty agreement" validator would surface every
    # taxonomy_mismatch packet as an unplanted second conflict.
    for seed in range(20):
        planted = plant_taxonomy_specialty_mismatch(_clean_spec(), Random(seed))
        expected_board = SPECIALTY_CATALOG[planted.board_cert_fields.specialty].board_acronym
        assert planted.board_cert_fields.board == expected_board, (
            f"seed {seed}: board {planted.board_cert_fields.board!r} "
            f"doesn't certify {planted.board_cert_fields.specialty!r}"
        )


def test_plant_taxonomy_mismatch_marker_shape() -> None:
    planted = plant_taxonomy_specialty_mismatch(_clean_spec(), Random(0))
    marker = planted.planted_conflicts[0]
    assert marker.keys() == _TAXONOMY_MARKER_KEYS
    assert marker["kind"] == "taxonomy_specialty_mismatch"
    assert marker["field"] == "boardCert.specialty"
    assert marker["sources"] == ["license", "boardCert"]
    assert marker["expectedSeverity"] == "Critical"
    assert marker["expected_to_flag"] is True


def test_plant_taxonomy_mismatch_taxonomy_code_is_nucc_shaped() -> None:
    planted = plant_taxonomy_specialty_mismatch(_clean_spec(), Random(0))
    code = planted.license_fields.taxonomy_code
    # NUCC codes are 10 chars, end in 'X', and start with digits.
    assert len(code) == 10
    assert code.endswith("X")


def test_plant_taxonomy_mismatch_deterministic_for_same_seed() -> None:
    a = plant_taxonomy_specialty_mismatch(_clean_spec(), Random(11))
    b = plant_taxonomy_specialty_mismatch(_clean_spec(), Random(11))
    assert a == b


# --- composition ---------------------------------------------------------

def test_planters_compose_into_two_markers() -> None:
    rng = Random(0)
    p = plant_name_variant(_clean_spec(), rng)
    p = plant_taxonomy_specialty_mismatch(p, rng)
    assert len(p.planted_conflicts) == 2
    kinds = [m["kind"] for m in p.planted_conflicts]
    assert kinds == ["name_variant", "taxonomy_specialty_mismatch"]
