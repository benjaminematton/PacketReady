"use client";

import type { Citation, Issue, Severity } from "@/lib/types";
import { cn } from "@/lib/utils";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from "@/components/ui/sheet";

/**
 * A single Issue. Card on the page, full detail in a side panel on click.
 *
 * <para>Client component because the panel's open/close state is interactive.
 * Each card owns its own Sheet so the parent page can stay a server component;
 * lifting selection up would require a client boundary at the route level.
 * The dismissable backdrop makes selection feel singleton — clicking another
 * card closes the current panel before opening the next.</para>
 *
 * <para>The card preview shows severity tag + message + a one-line remediation
 * preview. The sheet shows full remediation, validator name, and citations
 * (extracted_value strings in P1; document/page/bbox links in P3).</para>
 */
export function IssueCard({ issue }: { issue: Issue }) {
  return (
    <Sheet>
      <SheetTrigger
        className={cn(
          "group flex w-full items-start gap-4 rounded-lg border bg-card px-5 py-4 text-left text-card-foreground transition-colors",
          "hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          SEVERITY_BORDER[issue.severity],
        )}
      >
        <SeverityTag severity={issue.severity} />
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium">{issue.message}</p>
          <p className="mt-1 truncate text-xs text-muted-foreground">
            {issue.remediation}
          </p>
        </div>
        <span className="shrink-0 text-xs text-muted-foreground/70 group-hover:text-muted-foreground">
          {formatCitationCount(issue.citations.length)}
        </span>
      </SheetTrigger>

      <SheetContent side="right" className="w-full sm:max-w-md">
        <SheetHeader className="space-y-3 px-6">
          <div className="flex items-center gap-3">
            <SeverityTag severity={issue.severity} />
            <span className="font-mono text-xs uppercase tracking-wide text-muted-foreground">
              {issue.validator}
            </span>
          </div>
          <SheetTitle className="text-base font-medium leading-snug">
            {issue.message}
          </SheetTitle>
          <SheetDescription className="sr-only">
            Detail panel for an Issue emitted by validator {issue.validator}.
          </SheetDescription>
        </SheetHeader>

        <div className="space-y-6 px-6 pt-2 pb-6">
          <Section title="Remediation">
            <p className="text-sm leading-relaxed text-foreground/80">
              {issue.remediation}
            </p>
          </Section>

          <Section
            title={
              issue.citations.length === 1
                ? "Citation"
                : `Citations (${issue.citations.length})`
            }
          >
            {issue.citations.length === 0 ? (
              <p className="text-xs italic text-muted-foreground">
                No citations on this issue — typically a "missing data" finding
                where there's nothing to cite.
              </p>
            ) : (
              <ul className="space-y-2">
                {issue.citations.map((c, idx) => (
                  <li
                    key={`${c.sourceValidator}-${idx}`}
                    className="rounded border bg-muted/40 p-3"
                  >
                    <CitationBody citation={c} />
                  </li>
                ))}
              </ul>
            )}
          </Section>

          <Section title="Why we flagged this">
            <p className="text-xs leading-relaxed text-muted-foreground">
              Source-document highlighting lands in Phase 3 — the citation will
              link to the exact PDF page and bounding box of the extracted value.
            </p>
          </Section>
        </div>
      </SheetContent>
    </Sheet>
  );
}

function CitationBody({ citation }: { citation: Citation }) {
  const docRef = formatDocRef(citation);
  return (
    <>
      <p className="font-mono text-xs uppercase tracking-wide text-muted-foreground">
        {citation.sourceValidator}
      </p>
      <p className="mt-1 break-words font-mono text-xs text-foreground/90">
        {citation.extractedValue}
      </p>
      {docRef !== null && (
        <p className="mt-2 text-[11px] text-muted-foreground">{docRef}</p>
      )}
    </>
  );
}

function formatDocRef(c: Citation): string | null {
  if (c.documentId === null && c.page === null) return null;
  const parts: string[] = [];
  if (c.documentId !== null) parts.push(`Document ${c.documentId}`);
  if (c.page !== null) parts.push(`page ${c.page}`);
  return parts.join(" · ");
}

function formatCitationCount(n: number): string {
  return n === 1 ? "1 citation" : `${n} citations`;
}

function Section({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {title}
      </h3>
      {children}
    </section>
  );
}

function SeverityTag({ severity }: { severity: Severity }) {
  return (
    <span
      className={cn(
        "inline-flex shrink-0 items-center rounded-md px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wide",
        SEVERITY_TAG[severity],
      )}
    >
      {severity}
    </span>
  );
}

const SEVERITY_BORDER = {
  Critical: "border-rose-300 dark:border-rose-900",
  Major: "border-amber-300 dark:border-amber-900",
  Minor: "border-zinc-200 dark:border-zinc-800",
} satisfies Record<Severity, string>;

const SEVERITY_TAG = {
  Critical:
    "bg-rose-100 text-rose-800 dark:bg-rose-950 dark:text-rose-300",
  Major: "bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300",
  Minor: "bg-zinc-200 text-zinc-700 dark:bg-zinc-800 dark:text-zinc-300",
} satisfies Record<Severity, string>;
