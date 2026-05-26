import Link from "next/link";
import { notFound } from "next/navigation";
import { api } from "@/lib/api";
import type { AuditEventDto, Issue, Severity } from "@/lib/types";
import { ScoreBadge } from "@/components/score-badge";
import { IssueCard } from "@/components/issue-card";

// Operator-only surface in P1 — fixed en-US locale and the server's timezone.
// Revisit when the dashboard goes user-facing: render the timestamp in a
// client component and use the browser's locale + zone.
const DATE_FORMAT = new Intl.DateTimeFormat("en-US", {
  year: "numeric",
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
});

/**
 * Provider detail. Header shows the score and the breakdown counts; body lists
 * Issues, each opening a side-panel drill-in on click. The list is server-rendered
 * from a single API call; the side-panel client state lives inside each IssueCard.
 *
 * Issues come back already sorted (Severity DESC, then Validator name ASC) from
 * the API, so no re-sorting here.
 */
export default async function ProviderDetailPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;

  // Fetch detail + audit chain in parallel — the audit query only needs the
  // route id, not the resolved provider, so there's no dependency between them.
  // The audit fetch is best-effort: if it fails, the "Why we flagged this" tab
  // inside every IssueCard renders an empty state, but the score view still
  // loads. The detail fetch is load-bearing and is allowed to throw.
  const [provider, auditEvents] = await Promise.all([
    api.getProviderDetail(id),
    api.getProviderAudit(id).catch((): AuditEventDto[] => []),
  ]);
  if (provider === null) notFound();

  const score = provider.latestScore;

  return (
    <main className="mx-auto max-w-3xl px-6 py-10">
      <Link
        href="/providers"
        className="mb-6 inline-block text-xs text-zinc-500 hover:text-zinc-700 dark:text-zinc-400 dark:hover:text-zinc-200"
      >
        ← All providers
      </Link>

      <header className="mb-8 flex items-start justify-between gap-6">
        <div className="min-w-0 flex-1">
          <h1 className="text-2xl font-semibold tracking-tight">
            {provider.fullName}
          </h1>
          <dl className="mt-2 grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-sm text-zinc-600 dark:text-zinc-400">
            <dt className="text-zinc-500 dark:text-zinc-500">NPI</dt>
            <dd className="font-mono">{provider.npi}</dd>
            <dt className="text-zinc-500 dark:text-zinc-500">Credentialing</dt>
            <dd className="font-mono">{provider.credentialingState}</dd>
            {score && (
              <>
                <dt className="text-zinc-500 dark:text-zinc-500">Last scored</dt>
                <dd>{DATE_FORMAT.format(new Date(score.computedAt))}</dd>
              </>
            )}
          </dl>
        </div>
        <ScoreBadge
          score={score?.score ?? null}
          tier={score?.tier ?? null}
          className="h-10 min-w-16 text-base"
        />
      </header>

      {score === null ? (
        <NoScoreYet />
      ) : (
        <section>
          {score.issues.length > 0 && (
            <BreakdownBar
              critical={score.criticalCount}
              major={score.majorCount}
              minor={score.minorCount}
            />
          )}
          <IssuesList issues={score.issues} auditEvents={auditEvents} />
        </section>
      )}
    </main>
  );
}

function IssuesList({
  issues,
  auditEvents,
}: {
  issues: Issue[];
  auditEvents: AuditEventDto[];
}) {
  if (issues.length === 0) {
    return (
      <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-5 py-4 dark:border-emerald-900 dark:bg-emerald-950">
        <p className="text-sm font-medium text-emerald-900 dark:text-emerald-200">
          No issues found.
        </p>
        <p className="mt-1 text-xs text-emerald-800 dark:text-emerald-300">
          Provider passes every validator. Ready for payer submission.
        </p>
      </div>
    );
  }

  return (
    <ul className="space-y-2">
      {issues.map((issue, idx) => (
        // No stable Issue id from the API; (validator, index) is unique within
        // a sorted, deterministic list and stable across re-renders of the same DTO.
        <li key={`${issue.validator}-${idx}`}>
          <IssueCard issue={issue} auditEvents={auditEvents} />
        </li>
      ))}
    </ul>
  );
}

function BreakdownBar({
  critical,
  major,
  minor,
}: {
  critical: number;
  major: number;
  minor: number;
}) {
  return (
    <div className="mb-4 flex items-center gap-3 text-xs text-zinc-500 dark:text-zinc-400">
      <BreakdownPill severity="Critical" count={critical} />
      <BreakdownPill severity="Major" count={major} />
      <BreakdownPill severity="Minor" count={minor} />
    </div>
  );
}

function BreakdownPill({
  severity,
  count,
}: {
  severity: Severity;
  count: number;
}) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-md px-2 py-1 ${PILL_BG[severity]}`}
    >
      <span className="font-semibold tabular-nums">{count}</span>
      <span className="uppercase tracking-wide">{severity}</span>
    </span>
  );
}

const PILL_BG: Record<Severity, string> = {
  Critical: "bg-rose-100 text-rose-900 dark:bg-rose-950 dark:text-rose-300",
  Major: "bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-300",
  Minor: "bg-zinc-100 text-zinc-700 dark:bg-zinc-900 dark:text-zinc-300",
};

function NoScoreYet() {
  return (
    <div className="rounded-lg border border-dashed border-zinc-300 px-6 py-10 text-center dark:border-zinc-700">
      <p className="text-sm text-zinc-600 dark:text-zinc-400">
        No readiness score yet for this provider.
      </p>
      <p className="mt-2 text-xs text-zinc-500 dark:text-zinc-500">
        Score is generated on first <code className="font-mono">POST</code> to{" "}
        <code className="font-mono">/scores</code>; the seed runs it automatically.
      </p>
    </div>
  );
}
