"""Pin the scoring rules locked in the P2 doc."""

from __future__ import annotations

from runners.compare import compare_doc


def _by_field(results):
    return {r.field: r for r in results}


def test_exact_string_match() -> None:
    r = _by_field(compare_doc("license", {"state": "NY"}, {"state": "NY"}))
    assert r["state"].match


def test_status_is_case_sensitive() -> None:
    r = _by_field(compare_doc("license", {"status": "Active"}, {"status": "active"}))
    assert not r["status"].match


def test_missing_field_is_miss() -> None:
    r = _by_field(compare_doc("license", {"state": "NY"}, {}))
    assert not r["state"].match
    assert r["state"].extracted is None


def test_extra_extracted_field_is_ignored() -> None:
    results = compare_doc("license", {"state": "NY"}, {"state": "NY", "extra": "x"})
    assert {r.field for r in results} == {"state"}


def test_schedules_compare_as_multiset() -> None:
    same = _by_field(compare_doc(
        "dea",
        {"schedules": ["II", "III", "IV", "V"]},
        {"schedules": ["V", "IV", "III", "II"]},
    ))
    assert same["schedules"].match

    duplicate_mismatch = _by_field(compare_doc(
        "dea",
        {"schedules": ["II", "III"]},
        {"schedules": ["II", "II", "III"]},
    ))
    assert not duplicate_mismatch["schedules"].match


def test_list_vs_non_list_mismatch() -> None:
    r = _by_field(compare_doc(
        "dea",
        {"schedules": ["II"]},
        {"schedules": "II"},
    ))
    assert not r["schedules"].match


def test_list_type_mismatch_is_miss_not_match() -> None:
    """Tightened: `[1, 2]` does NOT satisfy a golden of `["1", "2"]`. The
    earlier `str()` coercion silently accepted mixed types and hid extractor
    bugs."""
    r = _by_field(compare_doc(
        "dea",
        {"schedules": ["1", "2"]},
        {"schedules": [1, 2]},
    ))
    assert not r["schedules"].match


def test_list_heterogeneous_elements_do_not_raise() -> None:
    """Mixed-type extractor output must score as a miss, not raise."""
    r = _by_field(compare_doc(
        "dea",
        {"schedules": ["II", "III"]},
        {"schedules": ["II", 3]},
    ))
    assert not r["schedules"].match


def test_present_flag_distinguishes_omitted_from_null() -> None:
    """The serialized row needs to tell us *why* a miss happened. An
    omitted key and an explicit `null` are different bugs in the extractor."""
    omitted = _by_field(compare_doc("license", {"state": "NY"}, {}))
    assert omitted["state"].present is False
    assert omitted["state"].extracted is None

    explicit_null = _by_field(compare_doc("license", {"state": "NY"}, {"state": None}))
    assert explicit_null["state"].present is True
    assert explicit_null["state"].extracted is None
    assert not explicit_null["state"].match
