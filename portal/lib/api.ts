// Typed server-side client for the .NET portal endpoints. NEVER import
// from a client component or pass through a Next.js `"use client"`
// boundary — that'd ship API_BASE_URL into the browser bundle. Both
// callers below run inside server components / server actions.
//
// The .NET API serves the portal at:
//   GET  /api/portal/{token}         → PortalStateDto | 410 magic_link_invalid
//   POST /api/portal/{token}/submit  → consume-ack    | 410 magic_link_invalid

// Default to the dev HTTP port. The HTTPS port (5066) needs a trusted
// dev cert, which Node's fetch refuses by default. Operators override
// in non-dev via `API_BASE_URL`.
const API_BASE_URL = process.env.API_BASE_URL ?? "http://localhost:5065";

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
};

export type PortalSubmitAck = {
  providerId: string;
  magicLinkId: string;
  consumedAt: string;
  turnJobId?: string;
};

/** Why a magic-link token failed validation. Matches MagicLinkInvalidReason from the .NET side, verbatim. */
export type MagicLinkInvalidReason =
  | "Malformed"
  | "BadSignature"
  | "NotFound"
  | "Expired"
  | "Consumed"
  | string; // string fallback so a new enum member doesn't crash the page

export type PortalError =
  | { kind: "magic_link_invalid"; reason: MagicLinkInvalidReason }
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
  const res = await fetch(`${API_BASE_URL}/api/portal/${tokenPath(token)}`, {
    cache: "no-store",
    headers: { Accept: "application/json" },
  });

  if (res.ok) return (await res.json()) as PortalState;
  return asPortalError(res);
}

export async function submitPortal(
  token: string,
): Promise<PortalSubmitAck | PortalError> {
  const res = await fetch(
    `${API_BASE_URL}/api/portal/${tokenPath(token)}/submit`,
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

  if (res.ok) return (await res.json()) as PortalSubmitAck;
  return asPortalError(res);
}
