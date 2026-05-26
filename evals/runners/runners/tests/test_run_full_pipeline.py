"""Unit coverage for the full-pipeline orchestrator.

Each test installs a per-request handler on httpx.MockTransport that
serves the three endpoints the orchestrator hits in sequence. The
orchestrator creates its own PipelineClient instances inside worker
threads, so we monkeypatch the constructor to inject MockTransport
for every instance.
"""

from __future__ import annotations

import json
from datetime import UTC, datetime, timedelta
from pathlib import Path

import httpx
import pytest

from runners import run_full_pipeline


# --- shared fixtures ------------------------------------------------------


def _write_golden(packet_dir: Path, *, packet_id: str, planted=()) -> None:
    """Minimal golden.json with the slice-2 identity block."""
    golden = {
        "goldenSchemaVersion": 2,
        "packetId": packet_id,
        "label": "Dr. Test",
        "identity": {
            "fullName": "Henry Anderson, MD",
            "npi": "1999000015",
            "dateOfBirth": "1975-03-14",
            "credentialingState": "NY",
        },
        "documents": [
            {"type": "license",    "filename": "license.pdf",     "fields": {}},
            {"type": "dea",        "filename": "dea.pdf",         "fields": {}},
            {"type": "boardCert",  "filename": "board-cert.pdf",  "fields": {}},
            {"type": "malpractice","filename": "malpractice.pdf", "fields": {}},
        ],
        "plantedConflicts": list(planted),
    }
    packet_dir.mkdir(parents=True, exist_ok=True)
    (packet_dir / "golden.json").write_text(json.dumps(golden))
    for filename in ("license.pdf", "dea.pdf", "board-cert.pdf", "malpractice.pdf"):
        (packet_dir / filename).write_bytes(b"%PDF-1.4 fake")


def _make_scenario_handler(score_response: dict, *, doc_id_prefix: str = "doc"):
    """Stock 200-response handler covering all 3 endpoints. doc_id_prefix
    lets a test inspect documentId → docType resolution without
    threading IDs through manually."""
    counter = {"n": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        path = req.url.path
        if path == "/api/providers" and req.method == "POST":
            return httpx.Response(201, json={"id": "prov-001"})
        if path.endswith("/documents") and req.method == "POST":
            counter["n"] += 1
            return httpx.Response(201, json={
                "documentId": f"{doc_id_prefix}-{counter['n']}",
                "providerId": "prov-001",
            })
        if path.endswith("/scores") and req.method == "POST":
            return httpx.Response(200, json=score_response)
        return httpx.Response(404, text=f"unmocked: {req.method} {path}")
    return handler


def _patch_pipeline_client(monkeypatch, handler) -> None:
    """Make every PipelineClient instance use MockTransport(handler)."""
    real_init = run_full_pipeline.PipelineClient.__init__

    def patched(self, *args, **kwargs):
        kwargs["transport"] = httpx.MockTransport(handler)
        real_init(self, *args, **kwargs)

    monkeypatch.setattr(run_full_pipeline.PipelineClient, "__init__", patched)


# --- happy path ------------------------------------------------------------


def test_run_happy_path_two_packets(tmp_path, monkeypatch):
    dataset = tmp_path / "dataset"
    _write_golden(dataset / "packet-001", packet_id="packet-001")
    _write_golden(
        dataset / "packet-002",
        packet_id="packet-002",
        planted=[{
            "kind": "name_variant",
            "field": "malpractice.fullName",
            "sources": ["license", "malpractice"],
            "expected_to_flag": True,
        }],
    )

    score_response = {
        "id": "score-001",
        "providerId": "prov-001",
        "score": 78,
        "tier": "Yellow",
        "issues": [],
    }
    _patch_pipeline_client(monkeypatch, _make_scenario_handler(score_response))

    payload = run_full_pipeline.run(
        dataset, concurrency=1, base_url="http://test")

    assert payload["packetCount"] == 2
    assert payload["successCount"] == 2
    assert payload["errorCount"] == 0
    assert {r["packetId"] for r in payload["scoreResults"]} == {
        "packet-001", "packet-002"}
    # conflicts block present with both kinds keyed, scoreResults
    # deterministically sorted.
    assert "conflicts" in payload
    assert {"name_variant", "taxonomy_specialty_mismatch"}.issubset(
        payload["conflicts"].keys())
    # No labels passed → no agreement block.
    assert "agreement" not in payload


def test_run_with_labels_emits_agreement(tmp_path, monkeypatch):
    dataset = tmp_path / "dataset"
    _write_golden(dataset / "packet-001", packet_id="packet-001")
    _write_golden(dataset / "packet-002", packet_id="packet-002")

    score_response = {
        "id": "score-001", "providerId": "p", "score": 90, "tier": "Green",
        "issues": [],
    }
    _patch_pipeline_client(monkeypatch, _make_scenario_handler(score_response))

    labels_path = tmp_path / "labels.json"
    labels_path.write_text(json.dumps({
        "_biasNote": "test",
        "labels": {"packet-001": "Green", "packet-002": "Green"},
    }))

    payload = run_full_pipeline.run(
        dataset, concurrency=1, labels_path=labels_path)

    assert "agreement" in payload
    assert payload["agreement"]["n"] == 2
    # Perfect tier agreement on both packets → κ=1.0, raw=1.0.
    assert payload["agreement"]["weightedKappa"] == 1.0
    assert payload["agreement"]["rawAgreement"] == 1.0


def test_run_mtime_gate_blocks_labels_newer_than_baseline(tmp_path, monkeypatch):
    dataset = tmp_path / "dataset"
    _write_golden(dataset / "packet-001", packet_id="packet-001")
    _write_golden(dataset / "packet-002", packet_id="packet-002")

    score_response = {
        "id": "s", "providerId": "p", "score": 50, "tier": "Yellow",
        "issues": [],
    }
    _patch_pipeline_client(monkeypatch, _make_scenario_handler(score_response))

    labels_path = tmp_path / "labels.json"
    labels_path.write_text(json.dumps({
        "labels": {"packet-001": "Yellow", "packet-002": "Yellow"},
    }))
    # Pretend baseline was generated an hour BEFORE the labels were
    # written (labels newer → anchoring gate fires).
    baseline_at = datetime.now(UTC) - timedelta(hours=1)

    with pytest.raises(run_full_pipeline.LabelsMtimeError):
        run_full_pipeline.run(
            dataset, concurrency=1, labels_path=labels_path,
            baseline_generated_at=baseline_at)


# --- error handling --------------------------------------------------------


def test_run_swallows_per_packet_contract_error_and_keeps_going(tmp_path, monkeypatch):
    dataset = tmp_path / "dataset"
    _write_golden(dataset / "packet-001", packet_id="packet-001")
    _write_golden(dataset / "packet-002", packet_id="packet-002")

    # First create succeeds, second create returns 400.
    call_count = {"create": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        path = req.url.path
        if path == "/api/providers" and req.method == "POST":
            call_count["create"] += 1
            if call_count["create"] == 2:
                return httpx.Response(400, json={
                    "type": "urn:packetready:error:invalid_provider_identity",
                    "violations": ["npi failed Luhn"],
                })
            return httpx.Response(201, json={"id": f"prov-{call_count['create']}"})
        if path.endswith("/documents"):
            return httpx.Response(201, json={"documentId": "d", "providerId": "p"})
        if path.endswith("/scores"):
            return httpx.Response(200, json={
                "id": "s", "providerId": "p", "score": 50, "tier": "Yellow",
                "issues": [],
            })
        return httpx.Response(404)

    _patch_pipeline_client(monkeypatch, handler)

    payload = run_full_pipeline.run(dataset, concurrency=1)

    assert payload["packetCount"] == 2
    assert payload["successCount"] == 1
    assert payload["errorCount"] == 1
    assert payload["errors"][0]["errorType"] == "PipelineContractError"
    # The successful packet's score still landed.
    assert len(payload["scoreResults"]) == 1


def test_run_missing_identity_in_golden_is_per_packet_error(tmp_path, monkeypatch):
    dataset = tmp_path / "dataset"
    # Hand-write a golden missing the identity block — slice 2 requires
    # it on every packet, but we test that the orchestrator surfaces
    # the failure inline rather than crashing the whole run.
    pd = dataset / "packet-001"
    pd.mkdir(parents=True)
    (pd / "golden.json").write_text(json.dumps({
        "packetId": "packet-001",
        "documents": [],
        # no `identity` block
    }))
    for f in ("license.pdf", "dea.pdf", "board-cert.pdf", "malpractice.pdf"):
        (pd / f).write_bytes(b"%PDF-1.4")

    _patch_pipeline_client(monkeypatch, _make_scenario_handler({
        "id": "s", "providerId": "p", "score": 50, "tier": "Yellow",
        "issues": [],
    }))

    payload = run_full_pipeline.run(dataset, concurrency=1)

    assert payload["successCount"] == 0
    assert payload["errorCount"] == 1
    assert payload["errors"][0]["errorType"] == "ScoreEvalContractError"
    assert "identity" in payload["errors"][0]["message"]


def test_run_empty_dataset_dir_fails_loud(tmp_path):
    empty = tmp_path / "empty"
    empty.mkdir()
    with pytest.raises(FileNotFoundError, match="no packet directories"):
        run_full_pipeline.run(empty, concurrency=1)


def test_run_nonexistent_dataset_dir_fails_loud(tmp_path):
    with pytest.raises(FileNotFoundError):
        run_full_pipeline.run(tmp_path / "does-not-exist", concurrency=1)


# --- payload shape ---------------------------------------------------------


def test_payload_carries_expected_top_level_keys(tmp_path, monkeypatch):
    dataset = tmp_path / "dataset"
    _write_golden(dataset / "packet-001", packet_id="packet-001")

    _patch_pipeline_client(monkeypatch, _make_scenario_handler({
        "id": "s", "providerId": "p", "score": 95, "tier": "Green",
        "issues": [],
    }))

    payload = run_full_pipeline.run(dataset, concurrency=1)

    expected_keys = {
        "datasetDir", "baseUrl", "ranAt",
        "packetCount", "successCount", "errorCount", "errors",
        "conflicts", "scoreResults",
    }
    assert expected_keys.issubset(payload.keys())
    # ranAt is RFC3339 / ISO 8601 with seconds precision.
    datetime.fromisoformat(payload["ranAt"])


def test_run_uses_payer_id_default_when_golden_omits_it(tmp_path, monkeypatch):
    dataset = tmp_path / "dataset"
    _write_golden(dataset / "packet-001", packet_id="packet-001")

    captured_payer: list[str] = []

    def handler(req: httpx.Request) -> httpx.Response:
        path = req.url.path
        if path == "/api/providers" and req.method == "POST":
            body = json.loads(req.content)
            captured_payer.append(body.get("payerId", "<missing>"))
            return httpx.Response(201, json={"id": "prov-001"})
        if path.endswith("/documents"):
            return httpx.Response(201, json={"documentId": "d"})
        if path.endswith("/scores"):
            return httpx.Response(200, json={
                "id": "s", "providerId": "p", "score": 80, "tier": "Yellow",
                "issues": [],
            })
        return httpx.Response(404)

    _patch_pipeline_client(monkeypatch, handler)

    run_full_pipeline.run(dataset, concurrency=1, payer_id="payer-b-state-medicaid")

    assert captured_payer == ["payer-b-state-medicaid"]


def test_score_results_sorted_for_diffable_output(tmp_path, monkeypatch):
    # Even with concurrency>1, the rollup must be deterministic so two
    # runs against the same data produce byte-identical results JSON.
    dataset = tmp_path / "dataset"
    for i in range(4):
        _write_golden(dataset / f"packet-{i:03d}", packet_id=f"packet-{i:03d}")

    _patch_pipeline_client(monkeypatch, _make_scenario_handler({
        "id": "s", "providerId": "p", "score": 70, "tier": "Yellow",
        "issues": [],
    }))

    payload = run_full_pipeline.run(dataset, concurrency=4)

    ids = [r["packetId"] for r in payload["scoreResults"]]
    assert ids == sorted(ids)
