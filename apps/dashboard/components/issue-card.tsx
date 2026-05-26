"use client";

import type { AuditEventDto, Citation, Issue, Severity } from "@/lib/types";
import { cn } from "@/lib/utils";
import { blobUrl } from "@/lib/blob-url";
import { PdfPreview } from "@/components/pdf-preview";
import { AuditTrail } from "@/components/audit-trail";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from "@/components/ui/sheet";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

/**
 * A single Issue. Card on the page, full detail in a side panel on click.
 *
 * <para>Client component because the panel's open/close state is interactive.
 * Each card owns its own Sheet so the parent page can stay a server component;
 * lifting selection up would require a client boundary at the route level.
 * The dismissable backdrop makes selection feel singleton — clicking another
 * card closes the current panel before opening the next.</para>
 *
 * <para>Hierarchy: a metadata strip on top (severity tag + low-conf pill +
 * validator name + citation count), the Issue message as the lede, the
 * remediation underneath, and a tier-colored stripe on the left edge. The
 * stripe is redundant with the severity tag — that's the point. The eye
 * lands on color first, then text.</para>
 */
export function IssueCard({
  issue,
  auditEvents = [],
}: {
  issue: Issue;
  /**
   * Provider-scoped audit chain, fetched once by the parent server component
   * and shared across every IssueCard on the page. Defaults to `[]` so
   * call-sites that haven't been updated still render — the "Why we flagged
   * this" tab just shows the empty-state placeholder.
   */
  auditEvents?: AuditEventDto[];
}) {
  return (
    <Sheet>
      <SheetTrigger
        className={cn(
          "group relative block w-full rounded-md border bg-card py-4 pl-5 pr-4 text-left transition-colors",
          "hover:bg-muted/40 focus-visible:bg-muted/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/40",
          SEVERITY_BORDER[issue.severity],
        )}
      >
        <span
          aria-hidden
          className={cn(
            "pointer-events-none absolute left-0 top-0 bottom-0 w-[3px] rounded-l-md",
            SEVERITY_STRIPE[issue.severity],
          )}
        />

        <div className="mb-2 flex items-center gap-3">
          <SeverityTag severity={issue.severity} />
          {issue.isLowConfidenceInput && <LowConfidencePill compact />}
          <span className="truncate font-mono text-[10px] uppercase tracking-[0.18em] text-muted-foreground">
            {issue.validator}
          </span>
          <span className="ml-auto inline-flex shrink-0 items-center gap-1.5 font-mono text-[10px] uppercase tracking-[0.18em] tabular-nums text-muted-foreground transition-colors group-hover:text-foreground/80">
            {formatCitationCount(issue.citations.length)}
            <span aria-hidden className="text-muted-foreground/50">
              ↗
            </span>
          </span>
        </div>

        <p className="text-[15px] font-semibold leading-snug text-foreground">
          {issue.message}
        </p>
        <p className="mt-1.5 line-clamp-2 text-[13px] leading-relaxed text-muted-foreground">
          {issue.remediation}
        </p>
      </SheetTrigger>

      <SheetContent side="right" className="w-full sm:max-w-xl">
        <SheetHeader className="space-y-3 border-b border-border/60 px-6 pb-4 pt-5">
          <div className="flex flex-wrap items-center gap-3">
            <SeverityTag severity={issue.severity} />
            {issue.isLowConfidenceInput && <LowConfidencePill />}
            <span className="font-mono text-[11px] uppercase tracking-[0.18em] text-muted-foreground">
              {issue.validator}
            </span>
          </div>
          <SheetTitle className="text-lg font-semibold leading-snug tracking-tight">
            {issue.message}
          </SheetTitle>
          <SheetDescription className="sr-only">
            Detail panel for an Issue emitted by validator {issue.validator}.
          </SheetDescription>
        </SheetHeader>

        {/*
          Two-tab split (P6 task 7). Drill-in carries the immediate why
          (remediation + citations with PDF previews); the audit tab shows
          the chain of system events behind the score this Issue lives in.
          Tab state is local to the Sheet — closing resets to Drill-in.
          Line variant: editorial underline indicator instead of a pill
          group, so the tabs read as a section spine rather than a control.
        */}
        <Tabs defaultValue="drill-in" className="px-6 pb-6 pt-4">
          <TabsList
            variant="line"
            className="h-9 w-full justify-start gap-6 border-b border-border/60 px-0"
          >
            <TabsTrigger
              value="drill-in"
              className="grow-0 px-0 font-mono text-[11px] font-medium uppercase tracking-[0.18em]"
            >
              Drill-in
            </TabsTrigger>
            <TabsTrigger
              value="audit"
              className="grow-0 px-0 font-mono text-[11px] font-medium uppercase tracking-[0.18em]"
            >
              Why we flagged this
            </TabsTrigger>
          </TabsList>

          <TabsContent value="drill-in" className="space-y-6 pt-5">
            {issue.isLowConfidenceInput && (
              <Section title="Downgraded from Critical">
                <p className="text-xs leading-relaxed text-muted-foreground">
                  One or more cited fields had extractor confidence below 0.85.
                  ConfidenceGuard downgraded this Issue from Critical to Minor —
                  the underlying signal is real but the input is noisy enough
                  that it shouldn&apos;t gate readiness on its own.
                </p>
              </Section>
            )}

            <Section title="Remediation">
              <p className="text-sm leading-relaxed text-foreground/85">
                {issue.remediation}
              </p>
            </Section>

            <Section
              title={
                issue.citations.length === 1
                  ? "Citation"
                  : `Citations · ${issue.citations.length}`
              }
            >
              {issue.citations.length === 0 ? (
                <p className="text-xs italic text-muted-foreground">
                  No citations on this issue — typically a &quot;missing
                  data&quot; finding where there&apos;s nothing to cite.
                </p>
              ) : (
                <ul className="space-y-3">
                  {issue.citations.map((c, idx) => (
                    <li
                      key={`${c.sourceValidator}-${idx}`}
                      className="rounded-md border border-border/60 bg-muted/30 p-3"
                    >
                      <CitationBody citation={c} />
                    </li>
                  ))}
                </ul>
              )}
            </Section>
          </TabsContent>

          <TabsContent value="audit" className="pt-5">
            <AuditTrail events={auditEvents} />
          </TabsContent>
        </Tabs>
      </SheetContent>
    </Sheet>
  );
}

function CitationBody({ citation }: { citation: Citation }) {
  const docRef = formatDocRef(citation);
  // Embed the source PDF page + bbox highlight when the citation carries
  // doc-ref fields (P3 extractor citations); pure-validator citations from
  // pre-extraction paths (Sanctions, etc.) just show the extracted-value text.
  // Building a narrowed shape here (instead of `documentId!` at the call site)
  // lets TypeScript prove the guard for us.
  const preview =
    citation.documentId !== null && citation.page !== null
      ? { documentId: citation.documentId, page: citation.page }
      : null;

  return (
    <>
      <div className="flex items-center gap-2">
        <p className="font-mono text-[10px] uppercase tracking-[0.18em] text-muted-foreground">
          {citation.sourceValidator}
        </p>
        {citation.lowConfidence && <LowConfidencePill compact />}
      </div>
      <p className="mt-1.5 break-words font-mono text-xs text-foreground/90">
        {citation.extractedValue}
      </p>
      {docRef !== null && (
        <p className="mt-2 font-mono text-[10px] uppercase tracking-[0.18em] text-muted-foreground">
          {docRef}
        </p>
      )}
      {preview !== null && (
        <div className="mt-3">
          <PdfPreview
            blobUrl={blobUrl(preview.documentId)}
            page={preview.page}
            bbox={citation.bbox}
            width={420}
          />
        </div>
      )}
    </>
  );
}

/**
 * Badge rendered next to the severity tag (and per-citation, in compact form)
 * when ConfidenceGuard flagged the input as low-confidence. Distinct visual
 * weight from severity tags so it reads as a modifier, not a category.
 */
function LowConfidencePill({ compact = false }: { compact?: boolean }) {
  return (
    <span
      title="Extractor confidence below 0.85"
      className={cn(
        "inline-flex shrink-0 items-center gap-1 rounded-sm border font-mono font-medium uppercase tracking-[0.18em]",
        "border-amber-300 bg-amber-50 text-amber-800",
        "dark:border-amber-900 dark:bg-amber-950 dark:text-amber-300",
        compact
          ? "px-1.5 py-0 text-[9px]"
          : "px-1.5 py-0.5 text-[10px]",
      )}
    >
      <span aria-hidden className="h-1 w-1 rounded-full bg-current opacity-70" />
      {compact ? "low conf" : "low confidence"}
    </span>
  );
}

function formatDocRef(c: Citation): string | null {
  if (c.documentId === null && c.page === null) return null;
  const parts: string[] = [];
  if (c.documentId !== null) parts.push(`doc ${c.documentId.slice(0, 8)}`);
  if (c.page !== null) parts.push(`p.${c.page}`);
  return parts.join(" · ");
}

function formatCitationCount(n: number): string {
  return n === 1 ? "1 cite" : `${n} cites`;
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
      <h3 className="mb-2.5 font-mono text-[10px] font-medium uppercase tracking-[0.22em] text-muted-foreground">
        {title}
      </h3>
      {children}
    </section>
  );
}

/**
 * Dotted severity label. Reads as a labeled instrument indicator rather than
 * a marketing badge — color lives in the dot, the word stays high-contrast.
 */
function SeverityTag({ severity }: { severity: Severity }) {
  return (
    <span
      className={cn(
        "inline-flex shrink-0 items-center gap-1.5 font-mono text-[10px] font-semibold uppercase tracking-[0.22em]",
        SEVERITY_TEXT[severity],
      )}
    >
      <span
        aria-hidden
        className={cn("h-1.5 w-1.5 rounded-full", SEVERITY_STRIPE[severity])}
      />
      {severity}
    </span>
  );
}

const SEVERITY_BORDER = {
  Critical: "border-rose-200 dark:border-rose-900/60",
  Major: "border-amber-200 dark:border-amber-900/60",
  Minor: "border-zinc-200 dark:border-zinc-800",
} satisfies Record<Severity, string>;

const SEVERITY_STRIPE = {
  Critical: "bg-rose-500",
  Major: "bg-amber-500",
  Minor: "bg-zinc-300 dark:bg-zinc-600",
} satisfies Record<Severity, string>;

const SEVERITY_TEXT = {
  Critical: "text-rose-700 dark:text-rose-400",
  Major: "text-amber-700 dark:text-amber-400",
  Minor: "text-zinc-600 dark:text-zinc-400",
} satisfies Record<Severity, string>;
