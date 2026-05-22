"""Pin the extractor client's contract enforcement.

`ExtractorContractError` is gate-breaking: a 200 OK body that doesn't carry a
`fields` JSON object means the server is silently miswired, and silently
coercing to `{}` would masquerade as an extractor that scored zero. These
tests lock the failure mode so a future refactor can't relax it.
"""

from __future__ import annotations

from pathlib import Path

import httpx
import pytest

from runners.extractor_client import ExtractorClient, ExtractorContractError


def _client_with(handler: httpx.MockTransport, *, base_url: str = "http://test") -> ExtractorClient:
    client = ExtractorClient(base_url=base_url)
    # Swap the underlying client to one with a mocked transport. Close the
    # original first so this helper doesn't leak a socket per test.
    client._client.close()
    client._client = httpx.Client(base_url=base_url, transport=handler)
    return client


def _pdf(tmp_path: Path) -> Path:
    p = tmp_path / "license.pdf"
    p.write_bytes(b"%PDF-1.4\n%not-a-real-pdf\n")
    return p


def test_extract_returns_fields_block(tmp_path: Path) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"fields": {"fullName": "Henry Anderson"}})

    with _client_with(httpx.MockTransport(handler)) as c:
        out = c.extract(_pdf(tmp_path), "license")
    assert out == {"fullName": "Henry Anderson"}


def test_missing_fields_key_raises_contract_error(tmp_path: Path) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"result": {}})

    with (
        _client_with(httpx.MockTransport(handler)) as c,
        pytest.raises(ExtractorContractError) as exc,
    ):
        c.extract(_pdf(tmp_path), "license")
    msg = str(exc.value)
    assert "fields" in msg and "license" in msg


def test_fields_not_object_raises_contract_error(tmp_path: Path) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"fields": ["fullName"]})

    with _client_with(httpx.MockTransport(handler)) as c, pytest.raises(ExtractorContractError):
        c.extract(_pdf(tmp_path), "dea")


def test_non_object_body_raises_contract_error(tmp_path: Path) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json=["nope"])

    with _client_with(httpx.MockTransport(handler)) as c, pytest.raises(ExtractorContractError):
        c.extract(_pdf(tmp_path), "boardCert")


def test_http_error_bubbles_as_httpstatuserror(tmp_path: Path) -> None:
    """Server-reported faults must NOT be wrapped — `ExtractorContractError`
    is reserved for 2xx-but-wrong-shape so the runner can attribute correctly.
    """
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(400, json={"title": "bad doctype"})

    with _client_with(httpx.MockTransport(handler)) as c, pytest.raises(httpx.HTTPStatusError):
        c.extract(_pdf(tmp_path), "license")
