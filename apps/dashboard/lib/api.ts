import { API_BASE_URL } from "./env";
import type {
  AuditEventDto,
  ProblemDetails,
  ProviderDetail,
  ProviderListItem,
  ReadinessScore,
} from "./types";
import { ProblemTypes } from "./types";

/**
 * Typed client for the PacketReady API. All methods are called from server
 * components — the browser never talks to the API directly in P1. Errors that
 * carry RFC 7807 ProblemDetails are wrapped in {@link ApiError} so callers can
 * branch on `error.type` (a stable URN) rather than status code or text.
 *
 * No caching here. Server components opt in to Next.js cache via the `next`
 * fetch option per call site — the score view stays fresh after a recompute,
 * the static list view can cache for short windows in P6 if it ever matters.
 *
 * Note: the `<T>` cast on the response is unchecked — manual types are the
 * contract with the .NET DTOs. Shape drift between API and dashboard will only
 * surface at the read site. `assertJsonObject` / `assertJsonArray` catch the
 * coarsest failure mode (wrong root kind) so it fails inside the wrapper.
 */

const DEFAULT_TIMEOUT_MS = 10_000;

export class ApiError extends Error {
  constructor(
    public readonly problem: ProblemDetails,
    public readonly statusCode: number,
  ) {
    super(`${problem.title} (${problem.type})`);
    this.name = "ApiError";
  }

  /** True when this is the canonical "no provider with that id" error. */
  get isProviderNotFound(): boolean {
    return this.problem.type === ProblemTypes.ProviderNotFound;
  }

  /**
   * True when the API rejected the id as syntactically invalid (e.g. the
   * all-zeros GUID). Treated as a not-found at the dashboard surface — from
   * the operator's perspective the row doesn't exist either way.
   */
  get isInvalidProviderId(): boolean {
    return this.problem.type === ProblemTypes.EmptyProviderId;
  }
}

type RequestInitWithNext = RequestInit & {
  next?: { revalidate?: number | false; tags?: string[] };
};

async function request<T>(
  path: string,
  init: RequestInitWithNext = {},
): Promise<T> {
  const url = `${API_BASE_URL}${path}`;

  const headers = new Headers(init.headers);
  if (!headers.has("Accept")) headers.set("Accept", "application/json");
  if (init.body != null && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  // Next.js ignores `next.revalidate` when cache is "no-store". Only apply the
  // RSC-friendly default when the caller hasn't asked for time-based caching.
  const wantsRevalidate = init.next?.revalidate != null;
  const cache = init.cache ?? (wantsRevalidate ? undefined : "no-store");

  const signal = init.signal ?? AbortSignal.timeout(DEFAULT_TIMEOUT_MS);

  const res = await fetch(url, { ...init, headers, cache, signal });

  if (!res.ok) {
    throw new ApiError(await readProblem(res, url), res.status);
  }

  const parsed: unknown = await res.json();
  return parsed as T;
}

/**
 * Read a non-2xx body as ProblemDetails. If the server emitted something else
 * (HTML 502 from a reverse proxy, etc.), synthesize a best-effort Problem and
 * stash a body snippet in `detail` so the failure is debuggable in logs.
 */
async function readProblem(res: Response, url: string): Promise<ProblemDetails> {
  const text = await res.text().catch(() => "");
  if (text.length > 0) {
    try {
      const parsed: unknown = JSON.parse(text);
      if (isProblemDetails(parsed)) return parsed;
    } catch {
      // fall through to synthesized problem
    }
  }
  return {
    type: "about:blank",
    title: `Unexpected ${res.status} from ${url}`,
    status: res.status,
    detail: text.length > 0 ? text.slice(0, 500) : undefined,
  };
}

function isProblemDetails(value: unknown): value is ProblemDetails {
  return (
    typeof value === "object" &&
    value !== null &&
    typeof (value as { type?: unknown }).type === "string" &&
    typeof (value as { title?: unknown }).title === "string"
  );
}

function assertJsonObject<T>(value: unknown, path: string): T {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`Expected JSON object from ${path}, got ${typeof value}.`);
  }
  return value as T;
}

function assertJsonArray<T>(value: unknown, path: string): T {
  if (!Array.isArray(value)) {
    throw new Error(`Expected JSON array from ${path}, got ${typeof value}.`);
  }
  return value as T;
}

export const api = {
  async listProviders(): Promise<ProviderListItem[]> {
    const path = "/api/providers";
    const data = await request<unknown>(path);
    return assertJsonArray<ProviderListItem[]>(data, path);
  },

  /**
   * Returns the provider detail, or `null` when the row doesn't exist. Both
   * "no such provider" (404) and "syntactically invalid id" (400 on the
   * all-zeros GUID) collapse to `null` here — the route layer renders the
   * same `not-found.tsx` for both, which matches operator intent.
   */
  async getProviderDetail(providerId: string): Promise<ProviderDetail | null> {
    const path = `/api/providers/${encodeURIComponent(providerId)}`;
    try {
      const data = await request<unknown>(path);
      return assertJsonObject<ProviderDetail>(data, path);
    } catch (err) {
      if (
        err instanceof ApiError &&
        (err.isProviderNotFound || err.isInvalidProviderId)
      ) {
        return null;
      }
      throw err;
    }
  },

  /** Recompute. Always writes a new score row; idempotent on the dashboard side. */
  async computeScore(providerId: string): Promise<ReadinessScore> {
    const path = `/api/providers/${encodeURIComponent(providerId)}/scores`;
    const data = await request<unknown>(path, { method: "POST" });
    return assertJsonObject<ReadinessScore>(data, path);
  },

  /**
   * Audit chain for one provider — backs the dashboard's "Why we flagged this"
   * tab. Returns `[]` (not null) when the provider has no audit rows yet; a
   * brand-new provider without a computed score has nothing to show, which
   * the panel renders as an empty-state placeholder.
   */
  async getProviderAudit(
    providerId: string,
    limit = 100,
  ): Promise<AuditEventDto[]> {
    const path = `/api/providers/${encodeURIComponent(providerId)}/audit?limit=${limit}`;
    const data = await request<unknown>(path);
    return assertJsonArray<AuditEventDto[]>(data, path);
  },
};
