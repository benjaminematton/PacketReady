"""Pin the golden.json shape checks."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from packetready_eval.identity import GOLDEN_SCHEMA_VERSION
from runners.golden import GoldenSchemaError, load_and_validate, validate


def _minimal() -> dict[str, Any]:
    # Mirror the shape `golden_for` emits: schema version, identity block,
    # at least one document. NPI 1234567893 is CMS-Luhn-valid (the same
    # placeholder the C# ProviderIdentityValidator pins).
    return {
        "goldenSchemaVersion": GOLDEN_SCHEMA_VERSION,
        "packetId": "p1",
        "identity": {
            "fullName": "Test Provider",
            "npi": "1234567893",
            "dateOfBirth": "1980-01-01",
            "credentialingState": "NY",
        },
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


def test_wrong_schema_version_fails() -> None:
    g = _minimal()
    g["goldenSchemaVersion"] = GOLDEN_SCHEMA_VERSION + 1
    with pytest.raises(GoldenSchemaError, match="goldenSchemaVersion"):
        validate(g)


def test_missing_identity_block_fails() -> None:
    g = _minimal()
    del g["identity"]
    with pytest.raises(GoldenSchemaError, match="identity"):
        validate(g)


def test_identity_with_non_luhn_npi_fails() -> None:
    g = _minimal()
    g["identity"]["npi"] = "0000000000"
    with pytest.raises(GoldenSchemaError, match="Luhn"):
        validate(g)


def test_identity_with_lowercase_state_fails() -> None:
    g = _minimal()
    g["identity"]["credentialingState"] = "ny"
    with pytest.raises(GoldenSchemaError, match="credentialingState"):
        validate(g)


def test_identity_with_future_dob_fails() -> None:
    g = _minimal()
    g["identity"]["dateOfBirth"] = "9999-01-01"
    with pytest.raises(GoldenSchemaError, match="dateOfBirth"):
        validate(g)


def test_identity_with_blank_full_name_fails() -> None:
    g = _minimal()
    g["identity"]["fullName"] = "   "
    with pytest.raises(GoldenSchemaError, match="fullName"):
        validate(g)
