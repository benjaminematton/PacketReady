"""HTTP client for the P4 score-side pipeline.

Path B (persisting): the orchestrator creates a Provider row, uploads
each PDF (which triggers extraction inline), then computes a readiness
score. Distinct from :class:`ExtractorClient` (Path A, stateless field
extraction used by the field-accuracy runner) — the score eval needs
persisted state to reach the validator + score-synthesis layer.

Endpoint surface this client talks to:

  - ``POST /api/providers``                            (slice 1)
  - ``POST /api/providers/{id}/documents``             (P3)
  - ``POST /api/providers/{id}/scores``                (P3)

The client is stateless across providers; per-packet state (provider
id, uploaded document id → docType map) is the caller's
responsibility. The orchestrator threads that map into
``score_metrics.build_packet_result`` so the conflict-metrics
``documentId → docType`` resolution lines up.

**Rate-limit handling.** The Anthropic-backed handlers can return 429
under bursty concurrency; this client retries 429s with exponential
backoff + jitter at the HTTP layer so the orchestrator's concurrency
cap is the actual budget. Non-429 4xx surfaces as
:class:`PipelineContractError`; 5xx surfaces as the underlying
:class:`httpx.HTTPStatusError` (transport-level retries don't help on
a server fault).

**No live-API dependency in tests.** Constructor takes an optional
:class:`httpx.BaseTransport`; tests inject :class:`httpx.MockTransport`
to script per-endpoint responses without touching the network.
"""

from __future__ import annotations

import random
import time
from contextlib import AbstractContextManager
from dataclasses import dataclass
from pathlib import Path
from types import TracebackType
from typing import Any

import httpx

from . import DEFAULT_BASE_URL


class PipelineContractError(RuntimeError):
    """Server returned 2xx but the body did not match the locked contract,
    OR returned 4xx with a typed problem we want to surface to the orchestrator
    (PayerNotConfigured, InvalidProviderIdentity, etc.). Distinct from
    :class:`ExtractorContractError` so failure attribution stays clean —
    the score-side pipeline is a different surface."""


@dataclass(frozen=True)
class CreateProviderResult:
    """Response from ``POST /api/providers``."""
    provider_id: str


@dataclass(frozen=True)
class UploadDocumentResult:
    """Response from ``POST /api/providers/{id}/documents``. The
    server returns more fields than this; we surface only what the
    orchestrator needs to build its documentId → docType map."""
    document_id: str
    doc_type: str


@dataclass(frozen=True)
class RetryConfig:
    """Backoff knobs. Defaults tuned for Anthropic's 429 cadence — a
    handful of seconds of stall in a worst-case run. Aggressive
    re-tuning belongs at the orchestrator level (lower concurrency),
    not deeper backoff here."""
    max_attempts: int = 4
    base_delay_seconds: float = 1.0
    max_delay_seconds: float = 16.0
    jitter_seconds: float = 0.5


class PipelineClient(AbstractContextManager["PipelineClient"]):
    """Thin httpx.Client wrapper for the create / upload / score
    sequence. One client serves a whole run; the orchestrator opens
    it once via ``with PipelineClient(...) as c:`` and reuses
    connections across all 50 packets."""

    def __init__(
        self,
        base_url: str = DEFAULT_BASE_URL,
        *,
        timeout_seconds: float = 60.0,
        retry: RetryConfig | None = None,
        transport: httpx.BaseTransport | None = None,
        rng: random.Random | None = None,
    ) -> None:
        # Per-packet calls (extract + score) can run ~10s each on Sonnet
        # vision; 60s top-level timeout leaves slack without hiding a
        # legitimate hang. Transport injection is the test seam — pass
        # MockTransport to exercise the client without a server.
        self._client = httpx.Client(
            base_url=base_url.rstrip("/"),
            timeout=timeout_seconds,
            transport=transport,
        )
        self._retry = retry or RetryConfig()
        # Inject the RNG so tests can pin jitter; production uses a
        # process-local Random keyed off the system clock.
        self._rng = rng or random.Random()

    # --- create -------------------------------------------------------

    def create_provider(
        self,
        *,
        payer_id: str | None = None,
        identity: dict[str, Any] | None = None,
    ) -> CreateProviderResult:
        """POST /api/providers with optional payerId + identity body.

        Both keys are omitted from the JSON when None — the server
        falls back to its defaults (Provider.DefaultPayerId /
        ProviderIdentityValidator.Placeholder). The orchestrator
        always passes identity for honest scoring; payerId defaults
        to payer-a-national-hmo per the Phase 4 decision.
        """
        body: dict[str, Any] = {}
        if payer_id is not None:
            body["payerId"] = payer_id
        if identity is not None:
            body["identity"] = identity

        resp = self._request_with_retry("POST", "/api/providers", json=body)
        data = self._expect_json_object(resp, "/api/providers")
        if "id" not in data or not isinstance(data["id"], str):
            raise PipelineContractError(
                f"POST /api/providers response missing string `id` "
                f"(got keys {list(data)})"
            )
        return CreateProviderResult(provider_id=data["id"])

    # --- upload -------------------------------------------------------

    def upload_document(
        self,
        provider_id: str,
        pdf_path: Path,
        *,
        doc_type: str,
    ) -> UploadDocumentResult:
        """POST /api/providers/{id}/documents with the PDF as a
        multipart file. The server runs classifier + extractor inline
        and persists; we only need the document id back to build the
        documentId → docType index. doc_type rides along for the
        caller's index — the server doesn't take it as a body param
        on this endpoint (it classifies the upload itself)."""
        with pdf_path.open("rb") as f:
            resp = self._request_with_retry(
                "POST",
                f"/api/providers/{provider_id}/documents",
                files={"file": (pdf_path.name, f, "application/pdf")},
            )
        data = self._expect_json_object(
            resp, f"/api/providers/{provider_id}/documents")
        document_id = data.get("documentId")
        if not isinstance(document_id, str):
            raise PipelineContractError(
                f"upload response missing string `documentId` "
                f"(provider={provider_id}, file={pdf_path.name})"
            )
        return UploadDocumentResult(document_id=document_id, doc_type=doc_type)

    # --- score --------------------------------------------------------

    def compute_score(self, provider_id: str) -> dict[str, Any]:
        """POST /api/providers/{id}/scores. Returns the raw
        ReadinessScoreDto JSON for the orchestrator to feed into
        ``score_metrics.build_packet_result`` /
        ``build_score_result``. We don't parse into a typed object
        here — the helpers in score_metrics already validate the
        wire shape and the orchestrator never inspects the dto
        directly otherwise."""
        resp = self._request_with_retry(
            "POST", f"/api/providers/{provider_id}/scores")
        return self._expect_json_object(
            resp, f"/api/providers/{provider_id}/scores")

    # --- internals ----------------------------------------------------

    def _request_with_retry(
        self,
        method: str,
        url: str,
        **kwargs: Any,
    ) -> httpx.Response:
        """Run a request, retrying 429s with exponential backoff +
        jitter. Non-429 status codes (success or hard fault) are
        returned without retry — caller branches on status.
        """
        last: httpx.Response | None = None
        for attempt in range(self._retry.max_attempts):
            resp = self._client.request(method, url, **kwargs)
            if resp.status_code != 429:
                return resp
            last = resp
            if attempt == self._retry.max_attempts - 1:
                break
            # Honor Retry-After if present (Anthropic sometimes sends it),
            # else fall back to exponential backoff.
            delay = self._compute_delay(attempt, resp.headers.get("Retry-After"))
            time.sleep(delay)
        # All retries exhausted on 429. Let the caller see the last
        # response so it can decide whether to abort the run or skip
        # the packet.
        assert last is not None
        return last

    def _compute_delay(self, attempt: int, retry_after: str | None) -> float:
        if retry_after is not None:
            try:
                # Anthropic uses seconds-as-int.
                return min(float(retry_after), self._retry.max_delay_seconds)
            except ValueError:
                pass
        base = self._retry.base_delay_seconds * (2 ** attempt)
        jitter = self._rng.uniform(0.0, self._retry.jitter_seconds)
        return min(base + jitter, self._retry.max_delay_seconds)

    def _expect_json_object(self, resp: httpx.Response, url: str) -> dict[str, Any]:
        """Validate a 2xx response body is a JSON object; surface
        4xx Problem Details as :class:`PipelineContractError` with
        the typed error body inline.
        """
        if resp.status_code >= 400:
            raise PipelineContractError(
                f"{url} returned {resp.status_code}: "
                f"{self._truncate(resp.text)}"
            )
        try:
            body = resp.json()
        except ValueError as exc:
            raise PipelineContractError(
                f"{url} returned non-JSON body: {self._truncate(resp.text)}"
            ) from exc
        if not isinstance(body, dict):
            raise PipelineContractError(
                f"{url} returned non-object body of type "
                f"{type(body).__name__}"
            )
        return body

    @staticmethod
    def _truncate(s: str, limit: int = 240) -> str:
        return s if len(s) <= limit else s[:limit] + "…"

    # --- lifecycle ----------------------------------------------------

    def close(self) -> None:
        self._client.close()

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> None:
        self.close()
