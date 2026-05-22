/**
 * Server-side environment access. `API_BASE_URL` is read once at module load so
 * server components don't re-evaluate process.env on every request. No
 * `NEXT_PUBLIC_` prefix — the dashboard's fetches happen in server components,
 * never in the browser. Any future client component that needs the API URL
 * requires a separately-named, deliberately-public env var.
 *
 * Outside of development we fail fast on a missing var: a silent
 * `http://localhost:5099` fallback in production would surface as opaque RSC
 * render failures rather than a clear startup error.
 */
function resolveApiBaseUrl(): string {
  const raw = process.env.API_BASE_URL;
  if (raw && raw.length > 0) {
    return raw.replace(/\/+$/, "");
  }
  if (process.env.NODE_ENV === "production") {
    throw new Error("API_BASE_URL is required in production.");
  }
  return "http://localhost:5099";
}

export const API_BASE_URL = resolveApiBaseUrl();
