/**
 * Browser-reachable URL for a document's PDF blob. Used from client components
 * (IssueCard's PdfPreview) which can't read the server-only `API_BASE_URL` —
 * a `NEXT_PUBLIC_*` env var is required for client visibility.
 *
 * Defaults to `http://localhost:5099` (the dev API) so a fresh checkout
 * "just works"; production overrides with `NEXT_PUBLIC_API_BASE_URL`. The
 * API URL isn't a secret — the dashboard server-side renders against it
 * anyway — so a public env var is honest about the dependency.
 */
export function blobUrl(documentId: string): string {
  const base = (
    process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5099"
  ).replace(/\/+$/, "");
  return `${base}/api/documents/${documentId}/blob`;
}
