// Typed server-side client for the .NET portal endpoints. NEVER import
// from a client component or pass through a Next.js `"use client"`
// boundary — that'd ship API_BASE_URL into the browser bundle. Both
// callers below run inside server components / server actions.
//
// The .NET API serves the portal at:
//   GET  /api/portal/{token}         → PortalStateDto | 410 magic_link_invalid
//   POST /api/portal/{token}/submit  → consume-ack    | 410 magic_link_invalid

// Dev defaults to the .NET HTTP port (5066's self-signed cert is rejected
// by Node's fetch). In production a missing `API_BASE_URL` is a config
// bug — fail at first request rather than silently dialing localhost.
//
// Resolved lazily (not at module load) so the build's `collect-page-data`
// step, which evaluates server modules under NODE_ENV=production without
// runtime env, doesn't fail the build for a missing var that will be
// present at deploy time.
function apiBaseUrl(): string {
  const fromEnv = process.env.API_BASE_URL;
  if (fromEnv && fromEnv.length > 0) return fromEnv;
  if (process.env.NODE_ENV === "production") {
    throw new Error(
      "API_BASE_URL is required in production but was not set.",
    );
  }
  return "http://localhost:5065";
}

// Server-component fetches inherit no default timeout. Without one a
// hung .NET process or unreachable host wedges the portal page until
// the upstream socket times out — minutes, not seconds. 10s is the
// budget for a single round-trip to the local API; bump if the portal
// ever fans out to a slower backend.
const FETCH_TIMEOUT_MS = 10_000;

export type IntakeState =
  | "Pending"
  | "AwaitingProvider"
  | "AgentProcessing"
  | "Complete"
  | "Escalated";

export type PortalState = {
  providerId: string;
  providerFullName: string | null;
  intakeSessionId: string;
  sessionState: IntakeState;
  linkIssuedAt: string;
  linkExpiresAt: string;
  documents: PortalDocument[];
};

/**
 * One uploaded document's view for the portal page. `latestExtraction`
 * is null when the document is on file but the extractor hasn't run /
 * failed — the card renders as "we have your file but haven't read it
 * yet" rather than dropping silently.
 */
export type PortalDocument = {
  documentId: string;
  /** PascalCase doc type from the classifier — `License | Dea | BoardCert | Malpractice | Cv | Other | Unknown`. */
  docType: string;
  /** 0..1 self-reported by the classifier; null when classification failed. */
  docTypeConfidence: number | null;
  originalName: string;
  pageCount: number;
  uploadedAt: string;
  latestExtraction: PortalExtraction | null;
};

/**
 * JSONB blobs travel as raw strings — the .NET side stores them that
 * way, and the portal parses each lazily when rendering. Parse failures
 * land as empty objects (the field row just doesn't render); they don't
 * blow up the page.
 */
export type PortalExtraction = {
  extractionId: string;
  schemaVersion: string;
  /** `{ fieldName: value, ... }`. Values are strings, numbers, arrays. */
  fieldsJson: string;
  /** `{ fieldName: { page, bbox: [x,y,w,h] }, ... }`. */
  fieldLocationsJson: string;
  /** `{ fieldName: 0..1, ... }`. Missing key = 0. */
  confidenceJson: string;
  extractedAt: string;
  /** Set when the row is confirmed for downstream consumption; LLM rows auto-confirm at write time. */
  confirmedAt: string | null;
};

export type PortalSubmitAck = {
  providerId: string;
  magicLinkId: string;
  consumedAt: string;
  turnJobId?: string;
};

/**
 * Why a magic-link token failed validation. Matches `MagicLinkInvalidReason`
 * from the .NET side. The wire value is a free-form string (an unknown
 * reason from a future API rev shouldn't crash the page); `formatReason`
 * in the page component owns the unknown-fallback copy and the
 * exhaustive switch's `default` arm is the single place that handles it.
 */
export type MagicLinkInvalidReason =
  | "Malformed"
  | "BadSignature"
  | "NotFound"
  | "Expired"
  | "Consumed";

export type PortalError =
  | {
      kind: "magic_link_invalid";
      /** Typed when the API returns a known reason; raw string otherwise. */
      reason: MagicLinkInvalidReason | (string & {});
    }
  | { kind: "transport"; status: number; detail: string };

function tokenPath(token: string): string {
  // Tokens are <base64url>.<base64url>. URL-safe alphabet only —
  // encodeURIComponent is a no-op for the legal token shape, but
  // belt-and-braces against a malformed input.
  return encodeURIComponent(token);
}

async function asPortalError(res: Response): Promise<PortalError> {
  // ProblemDetails body: { type, title, detail, status, ...extensions }.
  // For 410 magic_link_invalid the `reason` rides under extensions.
  if (res.status === 410) {
    type ProblemBody = { reason?: string };
    const body: ProblemBody = await res.json().catch(() => ({}));
    return {
      kind: "magic_link_invalid",
      reason: body.reason ?? "Unknown",
    };
  }

  const detail = await res.text().catch(() => "");
  return { kind: "transport", status: res.status, detail };
}

export async function fetchPortalState(
  token: string,
): Promise<PortalState | PortalError> {
  const res = await fetchOrTransport(
    `${apiBaseUrl()}/api/portal/${tokenPath(token)}`,
    { cache: "no-store", headers: { Accept: "application/json" } },
  );
  if ("kind" in res) return res;
  if (res.ok) return (await res.json()) as PortalState;
  return asPortalError(res);
}

export async function submitPortal(
  token: string,
): Promise<PortalSubmitAck | PortalError> {
  const res = await fetchOrTransport(
    `${apiBaseUrl()}/api/portal/${tokenPath(token)}/submit`,
    {
      method: "POST",
      cache: "no-store",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({}),
    },
  );
  if ("kind" in res) return res;
  if (res.ok) return (await res.json()) as PortalSubmitAck;
  return asPortalError(res);
}

// Wraps `fetch` with a timeout + transport-error normalization. Network
// errors (DNS, refused, reset) and timeouts both surface as
// `{ kind: 'transport', status, detail }` — the same shape `asPortalError`
// produces for non-410 HTTP failures — so callers handle one error model.
async function fetchOrTransport(
  url: string,
  init: RequestInit,
): Promise<Response | Extract<PortalError, { kind: "transport" }>> {
  try {
    return await fetch(url, {
      ...init,
      signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
    });
  } catch (err) {
    const detail = err instanceof Error ? err.message : String(err);
    // AbortError → 504-ish; other failures → 502-ish. The numeric status
    // is synthetic (no Response object exists) but mirrors the upstream
    // semantics callers will see in production.
    const status =
      err instanceof DOMException && err.name === "TimeoutError" ? 504 : 502;
    return { kind: "transport", status, detail };
  }
}
