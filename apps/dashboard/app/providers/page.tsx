import Link from "next/link";
import { api, ApiError } from "@/lib/api";
import type { ProviderListItem } from "@/lib/types";
import { ScoreBadge } from "@/components/score-badge";

const IS_DEV = process.env.NODE_ENV !== "production";

const DATE_FORMAT = new Intl.DateTimeFormat("en-US", {
  year: "numeric",
  month: "short",
  day: "numeric",
});

/**
 * Provider list. Sorted by score ascending (worst first) so the providers that
 * need attention land at the top — matches the "show me what to triage" UX the
 * dashboard exists for. Providers without a computed score sort last.
 *
 * Route is dynamic by virtue of the API client's `cache: "no-store"` default;
 * no `export const dynamic` needed.
 */
export default async function ProvidersPage() {
  const result = await loadProviders();

  return (
    <main className="mx-auto max-w-3xl px-6 py-10">
      <header className="mb-8">
        <h1 className="text-2xl font-semibold tracking-tight">Providers</h1>
        {result.kind === "ok" && (
          <p className="mt-1 text-sm text-zinc-500 dark:text-zinc-400">
            {result.rows.length} on file, sorted worst-first.
          </p>
        )}
      </header>

      {result.kind === "error" ? (
        <ApiDownNotice error={result.error} />
      ) : result.rows.length === 0 ? (
        <EmptyState />
      ) : (
        <ProviderList rows={result.rows} />
      )}
    </main>
  );
}

type LoadResult =
  | { kind: "ok"; rows: ProviderListItem[] }
  | { kind: "error"; error: unknown };

async function loadProviders(): Promise<LoadResult> {
  try {
    const rows = await api.listProviders();
    return { kind: "ok", rows: [...rows].sort(sortWorstFirst) };
  } catch (error) {
    return { kind: "error", error };
  }
}

function ProviderList({ rows }: { rows: ProviderListItem[] }) {
  return (
    <ul className="divide-y divide-zinc-200 rounded-lg border border-zinc-200 bg-white dark:divide-zinc-800 dark:border-zinc-800 dark:bg-zinc-950">
      {rows.map((p) => (
        <li key={p.id}>
          <Link
            href={`/providers/${p.id}`}
            className="flex items-center justify-between gap-4 px-5 py-4 hover:bg-zinc-50 dark:hover:bg-zinc-900"
          >
            <div className="min-w-0 flex-1">
              <p className="truncate text-base font-medium text-zinc-900 dark:text-zinc-100">
                {p.fullName}
              </p>
              <p className="mt-0.5 text-xs text-zinc-500 dark:text-zinc-400">
                {p.latestComputedAt
                  ? `Last scored ${DATE_FORMAT.format(new Date(p.latestComputedAt))}`
                  : "Not yet scored"}
              </p>
            </div>
            <ScoreBadge score={p.latestScore} tier={p.latestTier} />
          </Link>
        </li>
      ))}
    </ul>
  );
}

function EmptyState() {
  return (
    <div className="rounded-lg border border-dashed border-zinc-300 px-6 py-12 text-center dark:border-zinc-700">
      <p className="text-sm text-zinc-600 dark:text-zinc-400">
        No providers yet.
      </p>
      {IS_DEV && (
        <p className="mt-2 text-xs text-zinc-500 dark:text-zinc-500">
          Run{" "}
          <code className="rounded bg-zinc-100 px-1.5 py-0.5 font-mono text-xs dark:bg-zinc-800">
            dotnet run --project tools/Seed
          </code>{" "}
          from the repo root to load the P1 fixtures.
        </p>
      )}
    </div>
  );
}

function ApiDownNotice({ error }: { error: unknown }) {
  const message =
    error instanceof ApiError
      ? `${error.problem.title} (${error.statusCode})`
      : error instanceof Error
        ? error.message
        : "Unknown error";

  return (
    <div className="rounded-lg border border-rose-200 bg-rose-50 px-5 py-4 dark:border-rose-900 dark:bg-rose-950">
      <p className="text-sm font-medium text-rose-900 dark:text-rose-200">
        Couldn&apos;t reach the API.
      </p>
      <p className="mt-1 text-xs text-rose-800 dark:text-rose-300">{message}</p>
      {IS_DEV && (
        <p className="mt-3 text-xs text-rose-700 dark:text-rose-400">
          Start it with{" "}
          <code className="rounded bg-rose-100 px-1.5 py-0.5 font-mono dark:bg-rose-900/50">
            dotnet run --project apps/api/Api/Api.csproj
          </code>
          .
        </p>
      )}
    </div>
  );
}

function sortWorstFirst(a: ProviderListItem, b: ProviderListItem): number {
  // Both-null branch is here for sort stability; types allow it, the seed data
  // makes it unreachable. Unscored providers sink to the bottom.
  if (a.latestScore === null && b.latestScore === null) return 0;
  if (a.latestScore === null) return 1;
  if (b.latestScore === null) return -1;
  return a.latestScore - b.latestScore;
}
