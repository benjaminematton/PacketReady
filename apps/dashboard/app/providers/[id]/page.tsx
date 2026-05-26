import Link from "next/link";
import { notFound } from "next/navigation";
import { api } from "@/lib/api";
import type {
  AuditEventDto,
  Issue,
  ProviderDetail,
  ReadinessScore,
  Severity,
  Tier,
} from "@/lib/types";
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
        className="mb-6 inline-flex items-center gap-1.5 font-mono text-[11px] uppercase tracking-[0.18em] text-zinc-500 transition-colors hover:text-zinc-900 dark:text-zinc-500 dark:hover:text-zinc-200"
      >
        ← Back to triage queue
      </Link>

      <ProviderMasthead provider={provider} score={score} />

      {score === null ? (
        <NoScoreYet />
      ) : (
        <section>
          {score.issues.length > 0 && (
            <BreakdownStrip
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

/**
 * Doc-spine masthead with a hero readiness-score card on the right. Mirrors
 * the §2 spine table from `docs/style.md` so the detail page reads as a
 * continuation of the providers list, not a different product. The score
 * card is the demo's iconic moment — large mono tabular numeral, tier label
 * underneath, tier stripe on the left edge.
 */
function ProviderMasthead({
  provider,
  score,
}: {
  provider: ProviderDetail;
  score: ReadinessScore | null;
}) {
  return (
    <header className="mb-8 border-b border-zinc-200 pb-7 dark:border-zinc-800">
      <div className="flex items-start justify-between gap-6">
        <div className="min-w-0 flex-1">
          <p className="font-mono text-[11px] uppercase tracking-[0.18em] text-zinc-500 dark:text-zinc-500">
            Credentialing chart
          </p>
          <h1 className="mt-1.5 text-2xl font-semibold tracking-tight text-zinc-900 dark:text-zinc-100">
            {provider.fullName}
          </h1>
          <dl className="mt-5 grid grid-cols-[5rem_1fr] gap-x-4 gap-y-1.5 font-mono text-[12px]">
            <SpineRow term="NPI" definition={provider.npi} />
            <SpineRow term="State" definition={provider.credentialingState} />
            {score && (
              <SpineRow
                term="Scored"
                definition={DATE_FORMAT.format(new Date(score.computedAt))}
              />
            )}
          </dl>
        </div>
        {score ? (
          <HeaderScoreCard score={score} />
        ) : (
          <ScoreBadge score={null} tier={null} className="h-10 min-w-16" />
        )}
      </div>
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
  Red: "border-rose-500",
  Yellow: "border-amber-500",
  Green: "border-emerald-500",
};

const TIER_TEXT: Record<Tier, string> = {
  Red: "text-rose-700 dark:text-rose-400",
  Yellow: "text-amber-700 dark:text-amber-400",
  Green: "text-emerald-700 dark:text-emerald-400",
};

const TIER_VERDICT: Record<Tier, string> = {
  Red: "submission blocked",
  Yellow: "needs review",
  Green: "submission ready",
};

function HeaderScoreCard({ score }: { score: ReadinessScore }) {
  return (
    <div
      className={`shrink-0 border-l-2 pl-5 ${TIER_STRIPE[score.tier]}`}
      aria-label={`Readiness score ${score.score} of 100, tier ${score.tier}`}
    >
      <p className="font-mono text-[10px] uppercase tracking-[0.22em] text-zinc-500 dark:text-zinc-500">
        Readiness score
      </p>
      <p className="mt-1 font-sans text-5xl font-bold tabular-nums tracking-tight text-zinc-900 dark:text-zinc-100">
        {score.score}
        <span className="ml-1 font-mono text-base font-normal text-zinc-400 dark:text-zinc-600">
          /100
        </span>
      </p>
      <p
        className={`mt-2 font-mono text-[10px] font-semibold uppercase tracking-[0.22em] ${TIER_TEXT[score.tier]}`}
      >
        {score.tier} · {TIER_VERDICT[score.tier]}
      </p>
    </div>
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
    return <NoIssuesFound />;
  }

  return (
    <ul className="space-y-2.5">
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

function BreakdownStrip({
  critical,
  major,
  minor,
}: {
  critical: number;
  major: number;
  minor: number;
}) {
  return (
    <div className="mb-5 flex w-fit items-center gap-5 border-y border-zinc-200 py-2 font-mono text-[11px] uppercase tracking-[0.18em] dark:border-zinc-800">
      <BreakdownItem severity="Critical" count={critical} />
      <Divider />
      <BreakdownItem severity="Major" count={major} />
      <Divider />
      <BreakdownItem severity="Minor" count={minor} />
    </div>
  );
}

const SEVERITY_DOT: Record<Severity, string> = {
  Critical: "bg-rose-600",
  Major: "bg-amber-500",
  Minor: "bg-zinc-400 dark:bg-zinc-500",
};

function BreakdownItem({
  severity,
  count,
}: {
  severity: Severity;
  count: number;
}) {
  return (
    <span className="inline-flex items-center gap-2 text-zinc-700 dark:text-zinc-300">
      <span
        className={`h-2 w-2 rounded-full ${SEVERITY_DOT[severity]}`}
        aria-hidden
      />
      <span className="tabular-nums text-sm font-semibold normal-case tracking-normal text-zinc-900 dark:text-zinc-100">
        {count}
      </span>
      <span>{severity}</span>
    </span>
  );
}

function Divider() {
  return (
    <span
      aria-hidden
      className="h-3 w-px bg-zinc-300 dark:bg-zinc-700"
    />
  );
}

function NoIssuesFound() {
  return (
    <div className="border-l-2 border-emerald-500 bg-emerald-50/50 px-5 py-4 dark:bg-emerald-950/20">
      <p className="font-mono text-[11px] uppercase tracking-[0.22em] text-emerald-700 dark:text-emerald-400">
        Submission ready
      </p>
      <p className="mt-2 text-sm font-medium text-emerald-900 dark:text-emerald-200">
        No issues found.
      </p>
      <p className="mt-1 text-xs text-emerald-800 dark:text-emerald-300">
        Provider passes every validator.
      </p>
    </div>
  );
}

function NoScoreYet() {
  return (
    <div className="border-y border-zinc-200 px-6 py-14 text-center dark:border-zinc-800">
      <p className="font-mono text-[11px] uppercase tracking-[0.22em] text-zinc-500 dark:text-zinc-500">
        Awaiting compute
      </p>
      <p className="mt-3 text-sm text-zinc-600 dark:text-zinc-400">
        No readiness score yet for this provider.
      </p>
      <p className="mt-2 font-mono text-[11px] text-zinc-500 dark:text-zinc-500">
        Score is generated on first{" "}
        <code className="rounded-sm bg-zinc-100 px-1.5 py-0.5 dark:bg-zinc-800">
          POST /scores
        </code>
        ; the seed runs it automatically.
      </p>
    </div>
  );
}
