"""Pin the golden.json shape checks."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from runners.golden import GoldenSchemaError, load_and_validate, validate


def _minimal() -> dict[str, Any]:
    return {
        "packetId": "p1",
        "documents": [
            {"type": "license", "filename": "license.pdf", "fields": {"state": "NY"}},
        ],
    }


def test_minimal_valid() -> None:
    validate(_minimal())  # does not raise


def test_missing_top_key() -> None:
    g = _minimal()
    del g["documents"]
    with pytest.raises(GoldenSchemaError, match="documents"):
        validate(g)


def test_unknown_doc_type() -> None:
    g = _minimal()
    g["documents"][0]["type"] = "passport"
    with pytest.raises(GoldenSchemaError, match="passport"):
        validate(g)


def test_filename_must_match_schema() -> None:
    g = _minimal()
    g["documents"][0]["filename"] = "license-v2.pdf"
    with pytest.raises(GoldenSchemaError, match="license.pdf"):
        validate(g)


def test_duplicate_doc_type() -> None:
    g = _minimal()
    g["documents"].append(
        {"type": "license", "filename": "license.pdf", "fields": {}}
    )
    with pytest.raises(GoldenSchemaError, match="duplicate"):
        validate(g)


def test_load_invalid_json(tmp_path: Path) -> None:
    p = tmp_path / "golden.json"
    p.write_text("{not json", encoding="utf-8")
    with pytest.raises(GoldenSchemaError, match="invalid JSON"):
        load_and_validate(p)


def test_load_valid(tmp_path: Path) -> None:
    p = tmp_path / "golden.json"
    p.write_text(json.dumps(_minimal()), encoding="utf-8")
    assert load_and_validate(p)["packetId"] == "p1"
