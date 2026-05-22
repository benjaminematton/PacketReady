"""HTTP client for the P3 extractor endpoint.

P2 contract — the SHAPE is locked, the BODY is P3's problem:

    POST /api/extract  multipart/form-data
        file:    PDF bytes (form field "file")
        docType: license | dea | boardCert | malpractice | cv

    200 OK  application/json  { "fields": { ... } }
    4xx     application/problem+json  RFC 7807 ProblemDetails
"""

from __future__ import annotations

from contextlib import AbstractContextManager
from pathlib import Path
from types import TracebackType
from typing import Any

import httpx

from . import DEFAULT_BASE_URL


class ExtractorContractError(RuntimeError):
    """Server returned 200 but the body did not match the locked contract.

    Distinct from `httpx.HTTPStatusError` (transport/HTTP layer) and
    `httpx.RequestError` (connect/timeout) so the runner can attribute
    failures correctly without parsing exception messages.
    """


class ExtractorClient(AbstractContextManager["ExtractorClient"]):
    """Thin wrapper around `httpx.Client` for the extract endpoint.

    Connection-reusing by default — one `httpx.Client` underneath, so a
    full dataset pass keeps the socket warm. Use as a context manager
    (`with ExtractorClient() as c:`) so the underlying client is closed
    deterministically.
    """

    def __init__(
        self,
        base_url: str = DEFAULT_BASE_URL,
        timeout_seconds: float = 30.0,
    ) -> None:
        self._client = httpx.Client(
            base_url=base_url.rstrip("/"),
            timeout=timeout_seconds,
        )

    def extract(self, pdf_path: Path, doc_type: str) -> dict[str, Any]:
        """POST /api/extract and return the `fields` block.

        Raises:
            httpx.HTTPStatusError — non-2xx response (server reported a fault).
            ExtractorContractError — 2xx but body is missing `fields` or
                `fields` is not a JSON object. We fail loud rather than
                coercing to `{}`, because silent coercion produces a
                confusing "model is bad" score when the endpoint is broken.
        """
        with pdf_path.open("rb") as f:
            resp = self._client.post(
                "/api/extract",
                files={"file": (pdf_path.name, f, "application/pdf")},
                data={"docType": doc_type},
            )
        resp.raise_for_status()

        body = resp.json()
        if not isinstance(body, dict) or "fields" not in body:
            raise ExtractorContractError(
                f"extractor response missing `fields` key "
                f"(doc_type={doc_type!r}, path={pdf_path.name}, body_keys={list(body) if isinstance(body, dict) else type(body).__name__})"
            )
        fields = body["fields"]
        if not isinstance(fields, dict):
            raise ExtractorContractError(
                f"extractor `fields` must be a JSON object, got {type(fields).__name__} "
                f"(doc_type={doc_type!r}, path={pdf_path.name})"
            )
        return fields

    def close(self) -> None:
        self._client.close()

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> None:
        self.close()
