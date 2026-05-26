"""Unit coverage for PipelineClient — stubbed httpx, no network.

Each test installs a per-request handler on httpx.MockTransport that
inspects the incoming request and returns the desired response. This
exercises the full HTTP serialization path (JSON encoding, multipart,
status code branching) without a live API.
"""

from __future__ import annotations

import json
import random
import time
from pathlib import Path

import httpx
import pytest

from runners.pipeline_client import (
    CreateProviderResult,
    PipelineClient,
    PipelineContractError,
    RetryConfig,
    UploadDocumentResult,
)


def _make_client(
    handler,
    *,
    retry: RetryConfig | None = None,
    rng_seed: int = 42,
) -> PipelineClient:
    return PipelineClient(
        base_url="http://test",
        transport=httpx.MockTransport(handler),
        retry=retry,
        rng=random.Random(rng_seed),
    )


# --- create_provider -----------------------------------------------------


def test_create_provider_no_body_sends_empty_object():
    captured: list[httpx.Request] = []

    def handler(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(201, json={"id": "00000000-0000-0000-0000-000000000001"})

    with _make_client(handler) as client:
        result = client.create_provider()

    assert isinstance(result, CreateProviderResult)
    assert result.provider_id == "00000000-0000-0000-0000-000000000001"

    body = json.loads(captured[0].content)
    assert body == {}  # both keys omitted when None


def test_create_provider_with_identity_and_payer_serializes_both():
    captured: list[dict] = []

    def handler(req: httpx.Request) -> httpx.Response:
        captured.append(json.loads(req.content))
        return httpx.Response(201, json={"id": "abc-def"})

    identity = {
        "fullName": "Henry Anderson, MD",
        "npi": "1999000015",
        "dateOfBirth": "1975-03-14",
        "credentialingState": "NY",
    }
    with _make_client(handler) as client:
        client.create_provider(payer_id="payer-b-state-medicaid", identity=identity)

    assert captured[0] == {
        "payerId": "payer-b-state-medicaid",
        "identity": identity,
    }


def test_create_provider_400_surfaces_typed_problem():
    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(400, json={
            "type": "urn:packetready:error:invalid_provider_identity",
            "title": "Create-provider request failed validation.",
            "violations": ["npi failed the CMS Luhn (mod-10) check digit calculation."],
        })

    with _make_client(handler) as client:
        with pytest.raises(PipelineContractError, match="400"):
            client.create_provider()


def test_create_provider_422_surfaces_payer_not_configured():
    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(422, json={
            "type": "urn:packetready:error:payer_not_configured",
            "payerId": "payer-zzz",
            "knownPayerIds": ["payer-a-national-hmo", "payer-b-state-medicaid"],
        })

    with _make_client(handler) as client:
        with pytest.raises(PipelineContractError, match="422"):
            client.create_provider(payer_id="payer-zzz")


def test_create_provider_2xx_without_id_is_contract_error():
    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(201, json={"name": "drifted shape"})

    with _make_client(handler) as client:
        with pytest.raises(PipelineContractError, match="missing string `id`"):
            client.create_provider()


# --- upload_document -----------------------------------------------------


def test_upload_document_posts_multipart_and_returns_index(tmp_path: Path):
    pdf = tmp_path / "license.pdf"
    pdf.write_bytes(b"%PDF-1.4 fake")

    captured: list[httpx.Request] = []

    def handler(req: httpx.Request) -> httpx.Response:
        captured.append(req)
        return httpx.Response(201, json={
            "documentId": "doc-001",
            "providerId": "prov-xyz",
            "docType": "license",
        })

    with _make_client(handler) as client:
        result = client.upload_document(
            provider_id="prov-xyz", pdf_path=pdf, doc_type="license")

    assert result == UploadDocumentResult(document_id="doc-001", doc_type="license")
    # The body must be multipart/form-data with the PDF inline.
    ct = captured[0].headers["content-type"]
    assert ct.startswith("multipart/form-data")
    assert b"%PDF-1.4 fake" in captured[0].content


def test_upload_document_missing_document_id_is_contract_error(tmp_path: Path):
    pdf = tmp_path / "license.pdf"
    pdf.write_bytes(b"%PDF-1.4")

    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(201, json={"providerId": "prov-xyz"})  # no documentId

    with _make_client(handler) as client:
        with pytest.raises(PipelineContractError, match="documentId"):
            client.upload_document(provider_id="prov-xyz", pdf_path=pdf, doc_type="license")


# --- compute_score -------------------------------------------------------


def test_compute_score_returns_raw_dto():
    score_dto = {
        "id": "score-001",
        "providerId": "prov-xyz",
        "score": 78,
        "tier": "Yellow",
        "issues": [
            {"validator": "license_status", "field": "", "citations": []},
        ],
    }

    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json=score_dto)

    with _make_client(handler) as client:
        out = client.compute_score("prov-xyz")

    # Returned verbatim; score_metrics validates the shape, not the client.
    assert out == score_dto


def test_compute_score_404_surfaces_provider_not_found():
    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(404, json={
            "type": "urn:packetready:error:provider_not_found",
            "providerId": "prov-zzz",
        })

    with _make_client(handler) as client:
        with pytest.raises(PipelineContractError, match="404"):
            client.compute_score("prov-zzz")


# --- retry-on-429 --------------------------------------------------------


def test_request_retries_on_429_then_succeeds(monkeypatch):
    # Patch sleep so the test isn't actually sleeping seconds.
    sleeps: list[float] = []
    monkeypatch.setattr("runners.pipeline_client.time.sleep", lambda s: sleeps.append(s))

    call_count = {"n": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        call_count["n"] += 1
        if call_count["n"] < 3:
            return httpx.Response(429, text="rate-limited")
        return httpx.Response(201, json={"id": "ok"})

    with _make_client(handler, retry=RetryConfig(max_attempts=4, base_delay_seconds=0.1)) as client:
        result = client.create_provider()

    assert result.provider_id == "ok"
    assert call_count["n"] == 3
    # Two backoff sleeps happened (between attempts 1→2 and 2→3).
    assert len(sleeps) == 2


def test_request_gives_up_after_max_attempts(monkeypatch):
    monkeypatch.setattr("runners.pipeline_client.time.sleep", lambda s: None)

    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(429, text="rate-limited")

    with _make_client(handler, retry=RetryConfig(max_attempts=2, base_delay_seconds=0.1)) as client:
        # After 2 attempts, the 429 surfaces as a contract error.
        with pytest.raises(PipelineContractError, match="429"):
            client.create_provider()


def test_retry_after_header_honored(monkeypatch):
    sleeps: list[float] = []
    monkeypatch.setattr("runners.pipeline_client.time.sleep", lambda s: sleeps.append(s))

    call_count = {"n": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        call_count["n"] += 1
        if call_count["n"] == 1:
            return httpx.Response(429, headers={"Retry-After": "5"})
        return httpx.Response(201, json={"id": "ok"})

    with _make_client(handler) as client:
        client.create_provider()

    # Exactly one sleep, sized by the Retry-After header (5s),
    # not the exponential default.
    assert sleeps == [5.0]


def test_retry_after_caps_at_max_delay(monkeypatch):
    # A pathological Retry-After of 999s must clamp to max_delay_seconds.
    sleeps: list[float] = []
    monkeypatch.setattr("runners.pipeline_client.time.sleep", lambda s: sleeps.append(s))

    call_count = {"n": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        call_count["n"] += 1
        if call_count["n"] == 1:
            return httpx.Response(429, headers={"Retry-After": "999"})
        return httpx.Response(201, json={"id": "ok"})

    with _make_client(handler, retry=RetryConfig(max_delay_seconds=8.0)) as client:
        client.create_provider()

    assert sleeps == [8.0]


def test_non_429_4xx_does_not_retry(monkeypatch):
    # 400 / 422 are caller errors; retry won't help. Make sure we
    # don't waste budget hammering them.
    sleeps: list[float] = []
    monkeypatch.setattr("runners.pipeline_client.time.sleep", lambda s: sleeps.append(s))

    call_count = {"n": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        call_count["n"] += 1
        return httpx.Response(400, json={"violations": ["bad"]})

    with _make_client(handler) as client:
        with pytest.raises(PipelineContractError, match="400"):
            client.create_provider()

    assert call_count["n"] == 1
    assert sleeps == []


# --- shape: non-JSON, non-object body -----------------------------------


def test_non_json_2xx_body_is_contract_error():
    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(200, text="not json at all")

    with _make_client(handler) as client:
        with pytest.raises(PipelineContractError, match="non-JSON"):
            client.compute_score("prov-x")


def test_json_array_2xx_body_is_contract_error():
    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json=[1, 2, 3])

    with _make_client(handler) as client:
        with pytest.raises(PipelineContractError, match="non-object"):
            client.compute_score("prov-x")
