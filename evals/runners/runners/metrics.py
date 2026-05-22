"""Three rollups over a flat list of FieldResult rows.

Per-field, per-doc-type, per-packet — keyed exactly the way the results
JSON schema enumerates them, so the regression gate can compare apples
to apples. Field set comes from `packetready_eval.schema`; adding a key
there propagates here without code changes.

`None` vs `0.0`: a field that was scored zero times reports `None`
(absent from this run). A field that was scored but matched nothing
reports `0.0`. Downstream consumers must distinguish the two.
"""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass
from typing import Iterable

from packetready_eval.schema import DOC_TYPES, PER_FIELD_KEYS

from .compare import FieldResult

# Re-export for convenience; runners shouldn't need to know about the
# generators package layout to read the column list.
PER_DOC_TYPE_KEYS: tuple[str, ...] = DOC_TYPES

__all__ = ("PER_FIELD_KEYS", "PER_DOC_TYPE_KEYS", "Rollups", "rollup")


@dataclass(frozen=True)
class Rollups:
    per_field: dict[str, float | None]           # keyed "docType.fieldName"
    per_doc_type: dict[str, float | None]        # keyed docType
    per_packet: dict[str, float | None]          # keyed packetId


def _ratio(num: int, den: int) -> float | None:
    """Match-rate as a 4-decimal float, or `None` if nothing was scored.

    `None` means "no observations" — distinct from `0.0`, which means
    "scored, zero matches." Conflating them in earlier versions hid
    missing data behind a plausible-looking metric.
    """
    return None if den == 0 else round(num / den, 4)


def rollup(per_packet_results: dict[str, list[FieldResult]]) -> Rollups:
    field_matches: dict[str, int] = defaultdict(int)
    field_totals: dict[str, int] = defaultdict(int)
    doctype_matches: dict[str, int] = defaultdict(int)
    doctype_totals: dict[str, int] = defaultdict(int)
    packet_metrics: dict[str, float | None] = {}

    for packet_id, results in per_packet_results.items():
        packet_match = 0
        for r in results:
            key = f"{r.doc_type}.{r.field}"
            field_totals[key] += 1
            doctype_totals[r.doc_type] += 1
            if r.match:
                field_matches[key] += 1
                doctype_matches[r.doc_type] += 1
                packet_match += 1
        packet_metrics[packet_id] = _ratio(packet_match, len(results))

    per_field = {
        k: _ratio(field_matches.get(k, 0), field_totals.get(k, 0))
        for k in PER_FIELD_KEYS
    }
    per_doc_type = {
        k: _ratio(doctype_matches.get(k, 0), doctype_totals.get(k, 0))
        for k in PER_DOC_TYPE_KEYS
    }
    return Rollups(
        per_field=per_field,
        per_doc_type=per_doc_type,
        per_packet=packet_metrics,
    )
