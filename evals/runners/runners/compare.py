"""Per-field exact-match comparison between extractor output and golden.

Rules (locked in P2 doc):
  - Date normalization: `YYYY-MM-DD` only. No fuzzy date parsing.
  - List-valued fields (e.g. `schedules`): sorted-multiset equality —
    element values and counts must match; order does not. Element types
    must match what the golden declares; we do NOT coerce `[1, 2]` to
    match `["1", "2"]`.
  - Status enum: case-sensitive.
  - Extra extractor fields: IGNORED (golden defines the contract).
  - Missing extractor fields: COUNT AS MISS (fail-closed for declared fields).

The `extracted` value in a FieldResult can legitimately be `None` in two
distinct ways: the extractor *omitted* the key, or it *returned* `null`.
The `present` flag disambiguates so debugging traces don't conflate the
"key was never returned" and "key was returned as null" failure modes.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class FieldResult:
    doc_type: str
    field: str
    expected: Any
    extracted: Any
    match: bool
    # True when the extractor returned the key (value may still be `null`).
    # False when the key was absent from the response object entirely.
    present: bool


def _equal(expected: Any, extracted: Any) -> bool:
    """Field-level equality. Lists compare as sorted multisets — duplicates
    are preserved, but order is not. Element types must match; mixed-type
    or coerced inputs (e.g. `[1, 2]` vs `["1", "2"]`) are NOT equal.

    Everything else is `==` on values as-is (ISO date strings, enums, nums).
    """
    if isinstance(expected, list):
        if not isinstance(extracted, list) or len(expected) != len(extracted):
            return False
        # `sorted` on heterogeneous types raises in Python 3; guard explicitly
        # so a type-mismatched extractor output fails as a miss rather than
        # crashing the whole run.
        try:
            return sorted(expected) == sorted(extracted)
        except TypeError:
            return False
    return type(expected) is type(extracted) and expected == extracted


def compare_doc(
    doc_type: str,
    expected_fields: dict[str, Any],
    extracted_fields: dict[str, Any],
) -> list[FieldResult]:
    """One result row per field declared in `expected_fields`. Missing
    keys in `extracted_fields` are scored as a miss with `extracted=None`
    and `present=False`. Keys present in `extracted_fields` but absent
    from `expected_fields` are ignored.
    """
    results: list[FieldResult] = []
    for field, expected in expected_fields.items():
        present = field in extracted_fields
        extracted = extracted_fields.get(field)
        results.append(FieldResult(
            doc_type=doc_type,
            field=field,
            expected=expected,
            extracted=extracted,
            match=present and _equal(expected, extracted),
            present=present,
        ))
    return results


def flatten(results_by_packet: Iterable[Iterable[FieldResult]]) -> list[FieldResult]:
    """Flatten the nested per-packet → per-doc → per-field results."""
    out: list[FieldResult] = []
    for packet in results_by_packet:
        out.extend(packet)
    return out
