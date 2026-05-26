"""Validate `golden.json` shape before a run touches the extractor.

A malformed golden mid-run produces a stack trace pointing at whichever
field happened to be missing — confusing and slow. Validating up front
turns "KeyError partway through packet 3" into "packet-003: documents[2]
missing 'filename'" before a single PDF is opened.

The validator pins the required top-level keys, the doc-type whitelist
from `schema.py`, and the identity block's wire shape (so a malformed
`identity` fails here rather than as a 400 from POST /api/providers
mid-run). It does NOT enforce the *content* of `fields` (extra fields
are accepted; that flexibility is deliberate so non-scored hints can
ride along in goldens).
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

from packetready_eval.identity import (
    GOLDEN_SCHEMA_VERSION,
    validate_identity_dict,
)
from packetready_eval.schema import DOC_FILENAMES, DOC_TYPES


class GoldenSchemaError(ValueError):
    """A `golden.json` doesn't match the dataset contract."""


_REQUIRED_TOP_KEYS = ("goldenSchemaVersion", "packetId", "identity", "documents")
_REQUIRED_DOC_KEYS = ("type", "filename", "fields")


def validate(golden: dict[str, Any], *, source: str | Path = "<dict>") -> None:
    """Raise `GoldenSchemaError` if `golden` violates the contract.

    `source` is included verbatim in error messages so the caller can
    identify the offending packet (pass the path).
    """
    where = str(source)

    missing_top = [k for k in _REQUIRED_TOP_KEYS if k not in golden]
    if missing_top:
        raise GoldenSchemaError(f"{where}: missing top-level keys {missing_top}")

    # Hard-fail on an unknown schema version: a v1 golden in a v2 dataset
    # would otherwise be missing the identity block we depend on, and the
    # error surface would be much further downstream. Regeneration is in
    # lockstep with the bump, so any drift here is operator error.
    version = golden["goldenSchemaVersion"]
    if version != GOLDEN_SCHEMA_VERSION:
        raise GoldenSchemaError(
            f"{where}: goldenSchemaVersion={version!r} does not match the "
            f"current contract version {GOLDEN_SCHEMA_VERSION}. Regenerate "
            f"the dataset with `python -m packetready_eval.packets`."
        )

    if not isinstance(golden["packetId"], str) or not golden["packetId"]:
        raise GoldenSchemaError(f"{where}: packetId must be a non-empty string")

    identity_errors = validate_identity_dict(golden["identity"])
    if identity_errors:
        raise GoldenSchemaError(f"{where}: {'; '.join(identity_errors)}")

    documents = golden["documents"]
    if not isinstance(documents, list) or not documents:
        raise GoldenSchemaError(f"{where}: documents must be a non-empty list")

    seen_types: set[str] = set()
    for idx, doc in enumerate(documents):
        prefix = f"{where}: documents[{idx}]"
        if not isinstance(doc, dict):
            raise GoldenSchemaError(f"{prefix} must be an object")

        missing = [k for k in _REQUIRED_DOC_KEYS if k not in doc]
        if missing:
            raise GoldenSchemaError(f"{prefix} missing keys {missing}")

        doc_type = doc["type"]
        if doc_type not in DOC_TYPES:
            raise GoldenSchemaError(
                f"{prefix} type={doc_type!r} not in {list(DOC_TYPES)}"
            )
        if doc_type in seen_types:
            raise GoldenSchemaError(f"{prefix} duplicate type {doc_type!r}")
        seen_types.add(doc_type)

        expected_filename = DOC_FILENAMES[doc_type]
        if doc["filename"] != expected_filename:
            raise GoldenSchemaError(
                f"{prefix} filename={doc['filename']!r} "
                f"does not match schema (expected {expected_filename!r})"
            )

        if not isinstance(doc["fields"], dict):
            raise GoldenSchemaError(f"{prefix} fields must be an object")


def load_and_validate(path: Path) -> dict[str, Any]:
    """Read `path`, parse JSON, validate. Returns the parsed dict.

    JSON decode errors are surfaced as `GoldenSchemaError` so callers
    only need a single except clause.
    """
    import json

    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise GoldenSchemaError(f"{path}: invalid JSON ({exc.msg} at line {exc.lineno})") from exc

    if not isinstance(data, dict):
        raise GoldenSchemaError(f"{path}: top-level value must be an object")
    validate(data, source=path)
    return data
