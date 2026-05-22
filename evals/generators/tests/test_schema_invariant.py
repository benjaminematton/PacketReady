"""Lock the contract: serializer output keys === SCHEMA per doc type.

If this fails, either the SCHEMA in `packetready_eval.schema` and the
golden.json serializers in `packetready_eval.packets` have drifted, OR a
new field was added in only one place. Fix is always: change SCHEMA and
the matching `_xxx_json` helper together.
"""

from __future__ import annotations

from packetready_eval.packets import PACKET_SPECS, golden_for
from packetready_eval.schema import DOC_FILENAMES, DOC_TYPES, PER_FIELD_KEYS, SCHEMA


def test_every_doc_type_is_serialized_in_each_packet() -> None:
    for spec in PACKET_SPECS:
        doc_types_in_golden = {d["type"] for d in golden_for(spec)["documents"]}
        assert doc_types_in_golden == set(DOC_TYPES), (
            f"{spec.id}: documents emit {doc_types_in_golden}, schema declares {set(DOC_TYPES)}"
        )


def test_field_keys_match_schema_per_doc_type() -> None:
    for spec in PACKET_SPECS:
        for doc in golden_for(spec)["documents"]:
            expected = set(SCHEMA[doc["type"]])
            actual = set(doc["fields"].keys())
            assert actual == expected, (
                f"{spec.id} / {doc['type']}: fields {actual}, schema {expected}"
            )


def test_filenames_match_schema_per_doc_type() -> None:
    for spec in PACKET_SPECS:
        for doc in golden_for(spec)["documents"]:
            assert doc["filename"] == DOC_FILENAMES[doc["type"]], (
                f"{spec.id} / {doc['type']}: filename {doc['filename']!r} "
                f"!= {DOC_FILENAMES[doc['type']]!r}"
            )


def test_per_field_keys_cover_every_doc_field_exactly_once() -> None:
    derived = {f"{dt}.{f}" for dt in DOC_TYPES for f in SCHEMA[dt]}
    assert set(PER_FIELD_KEYS) == derived
    assert len(PER_FIELD_KEYS) == len(derived), "duplicate keys in PER_FIELD_KEYS"
