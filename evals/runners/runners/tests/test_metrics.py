"""Pin the rollup rules — especially the `None` vs `0.0` distinction."""

from __future__ import annotations

from runners.compare import FieldResult
from runners.metrics import PER_DOC_TYPE_KEYS, PER_FIELD_KEYS, rollup


def _r(doc_type: str, field: str, match: bool) -> FieldResult:
    return FieldResult(doc_type=doc_type, field=field, expected="x",
                       extracted="x" if match else None, match=match,
                       present=match)


def test_empty_dataset_yields_all_nones() -> None:
    rollups = rollup({})
    assert all(v is None for v in rollups.per_field.values())
    assert all(v is None for v in rollups.per_doc_type.values())
    assert rollups.per_packet == {}
    # Schema-stable column set even when there's no data.
    assert tuple(rollups.per_field) == PER_FIELD_KEYS
    assert tuple(rollups.per_doc_type) == PER_DOC_TYPE_KEYS


def test_packet_scored_but_all_miss_is_zero_not_none() -> None:
    rollups = rollup({"p1": [_r("license", "state", False)]})
    assert rollups.per_packet["p1"] == 0.0
    assert rollups.per_field["license.state"] == 0.0
    # Untouched fields still report None (no observations).
    assert rollups.per_field["license.licenseNumber"] is None


def test_partial_match_rate() -> None:
    rollups = rollup({"p1": [
        _r("license", "state", True),
        _r("license", "state", False),
        _r("license", "state", True),
    ]})
    assert rollups.per_field["license.state"] == round(2 / 3, 4)
    assert rollups.per_doc_type["license"] == round(2 / 3, 4)
    assert rollups.per_packet["p1"] == round(2 / 3, 4)
