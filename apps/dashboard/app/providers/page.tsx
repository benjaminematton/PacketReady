import Link from "next/link";
import { api, ApiError } from "@/lib/api";
import type { ProviderListItem, Tier } from "@/lib/types";
import { ScoreBadge } from "@/components/score-badge";

const IS_DEV = process.env.NODE_ENV !== "production";

const DATE_FORMAT = new Intl.DateTimeFormat("en-US", {
  year: "numeric",
  month: "short",
  day: "numeric",
});

const SNAPSHOT_FORMAT = new Intl.DateTimeFormat("en-US", {
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  hour12: false,
  timeZoneName: "short",
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
  const snapshotAt = SNAPSHOT_FORMAT.format(new Date());

  return (
    <main className="mx-auto max-w-3xl px-6 py-10">
      <PageMasthead
        snapshotAt={snapshotAt}
        rowCount={result.kind === "ok" ? result.rows.length : null}
      />

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

/**
 * Doc-spine-style header. Mirrors the §2 metadata table from docs/style.md so
 * the dashboard reads as a continuation of the docs, not a separate product.
 * The Data row is the compliance posture; it earns its spot per the same
 * "healthcare readers scan for data-handling first" rule.
 */
function PageMasthead({
  snapshotAt,
  rowCount,
}: {
  snapshotAt: string;
  rowCount: number | null;
}) {
  return (
    <header className="mb-8 border-b border-zinc-200 pb-6 dark:border-zinc-800">
      <div className="flex items-baseline justify-between gap-3">
        <h1 className="text-2xl font-semibold tracking-tight text-zinc-900 dark:text-zinc-100">
          Providers{" "}
          <span className="font-normal text-zinc-400 dark:text-zinc-600">
            — Triage queue
          </span>
        </h1>
        <span className="font-mono text-[11px] uppercase tracking-wider text-zinc-500 dark:text-zinc-500 tabular-nums">
          {snapshotAt}
        </span>
      </div>

      <dl className="mt-5 grid grid-cols-[5rem_1fr] gap-x-4 gap-y-1.5 font-mono text-[12px]">
        <SpineRow
          term="Status"
          definition={
            rowCount === null
              ? "API unreachable"
              : `Live · ${rowCount} on file`
          }
        />
        <SpineRow
          term="Data"
          definition="synthetic only · mocked PSV · no PHI"
        />
        <SpineRow term="Sort" definition="score asc · worst first" />
      </dl>
    </header>
  );
}

function SpineRow({
  term,
  definition,
}: {
  term: string;
  definition: string;
}) {
  return (
    <>
      <dt className="uppercase tracking-wider text-zinc-500 dark:text-zinc-500">
        {term}
      </dt>
      <dd className="text-zinc-700 dark:text-zinc-300">{definition}</dd>
    </>
  );
}

const TIER_STRIPE: Record<Tier, string> = {
  Red: "bg-rose-600",
  Yellow: "bg-amber-500",
  Green: "bg-emerald-600",
};

function ProviderList({ rows }: { rows: ProviderListItem[] }) {
  return (
    <ol className="divide-y divide-zinc-200 border-y border-zinc-200 dark:divide-zinc-800 dark:border-zinc-800">
      {rows.map((p, i) => (
        <li key={p.id}>
          <ProviderRow rank={i + 1} provider={p} />
        </li>
      ))}
    </ol>
  );
}

function ProviderRow({
  rank,
  provider,
}: {
  rank: number;
  provider: ProviderListItem;
}) {
  const stripe = provider.latestTier
    ? TIER_STRIPE[provider.latestTier]
    : "bg-zinc-200 dark:bg-zinc-800";
  const subline = provider.latestComputedAt
    ? `Scored ${DATE_FORMAT.format(new Date(provider.latestComputedAt))}`
    : "Not yet scored";

  return (
    <Link
      href={`/providers/${provider.id}`}
      className="group relative flex items-center gap-4 pl-5 pr-4 py-4 transition-colors hover:bg-zinc-50 dark:hover:bg-zinc-900"
    >
      <span
        aria-hidden="true"
        className={`absolute left-0 top-0 bottom-0 w-[2px] ${stripe}`}
      />
      <span className="font-mono text-[11px] tabular-nums text-zinc-400 dark:text-zinc-600 w-9 shrink-0">
        №{String(rank).padStart(2, "0")}
      </span>
      <div className="min-w-0 flex-1">
        <p className="truncate text-[15px] font-medium text-zinc-900 group-hover:text-zinc-950 dark:text-zinc-100 dark:group-hover:text-white">
          {provider.fullName}
        </p>
        <p className="mt-0.5 font-mono text-[11px] uppercase tracking-wider text-zinc-500 dark:text-zinc-500">
          {subline}
        </p>
      </div>
      <ScoreBadge
        score={provider.latestScore}
        tier={provider.latestTier}
        className="shrink-0"
      />
    </Link>
  );
}

function EmptyState() {
  return (
    <div className="border-y border-zinc-200 px-6 py-16 text-center dark:border-zinc-800">
      <p className="font-mono text-[11px] uppercase tracking-[0.2em] text-zinc-500 dark:text-zinc-500">
        Queue empty
      </p>
      <p className="mt-3 text-sm text-zinc-600 dark:text-zinc-400">
        No providers on file.
      </p>
      {IS_DEV && (
        <p className="mt-4 font-mono text-[11px] text-zinc-500 dark:text-zinc-500">
          Seed:{" "}
          <code className="rounded-sm bg-zinc-100 px-1.5 py-0.5 text-zinc-700 dark:bg-zinc-800 dark:text-zinc-300">
            dotnet run --project tools/Seed
          </code>
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
    <div className="border-l-2 border-rose-600 bg-rose-50/60 px-5 py-4 dark:bg-rose-950/30">
      <p className="font-mono text-[11px] uppercase tracking-[0.2em] text-rose-700 dark:text-rose-300">
        API unreachable
      </p>
      <p className="mt-2 text-sm font-medium text-rose-900 dark:text-rose-200">
        {message}
      </p>
      {IS_DEV && (
        <p className="mt-3 font-mono text-[11px] text-rose-800 dark:text-rose-400">
          Start:{" "}
          <code className="rounded-sm bg-rose-100/80 px-1.5 py-0.5 dark:bg-rose-900/40">
            dotnet run --project apps/api/Api/Api.csproj
          </code>
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
