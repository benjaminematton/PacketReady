import type { AuditEventDto } from "@/lib/types";
import { cn } from "@/lib/utils";

// Same-day chains (the demo case) render time-only; chains that span multiple
// calendar days prepend the date so the operator can tell "yesterday's score
// recompute" from "this morning's." Picked at the parent based on the actual
// span — no per-row branching downstream.
const TIME_FORMAT = new Intl.DateTimeFormat("en-US", {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hour12: false,
});

const DATETIME_FORMAT = new Intl.DateTimeFormat("en-US", {
  month: "short",
  day: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hour12: false,
});

/**
 * Vertical timeline of the audit chain for one provider. Server-passable
 * (no client state, no fetch); the parent `ProviderDetailPage` does the
 * single API call and shares the events with every IssueCard's panel.
 *
 * <para>One row per audit event in time order. Each row shows event type,
 * a parsed payload chip (per event type), and the wall-clock time. Marker
 * dots tint by event class — score-compute carries the tier color so the
 * "moment the score landed" is visible at a glance, document-upload events
 * stay neutral.</para>
 */
export function AuditTrail({ events }: { events: AuditEventDto[] }) {
  if (events.length === 0) {
    return <AuditEmpty />;
  }

  const formatter = spansMultipleDays(events) ? DATETIME_FORMAT : TIME_FORMAT;

  return (
    <ol className="relative space-y-4 border-l border-border/60 pl-5">
      {events.map((e) => {
        const parsed = tryParse(e.payload);
        const tone = markerTone(e.eventType, parsed);
        return (
          <li key={e.id} className="relative">
            <Marker tone={tone} />
            <div className="min-w-0">
              <div className="flex items-baseline justify-between gap-3">
                <p className="font-mono text-[11px] font-semibold uppercase tracking-[0.18em] text-foreground">
                  {e.eventType}
                </p>
                <p className="shrink-0 font-mono text-[10px] tabular-nums uppercase tracking-wider text-muted-foreground">
                  {formatter.format(new Date(e.occurredAt))}
                </p>
              </div>
              <PayloadSummary eventType={e.eventType} parsed={parsed} />
            </div>
          </li>
        );
      })}
    </ol>
  );
}

function AuditEmpty() {
  return (
    <div className="border border-dashed border-border/60 px-5 py-8 text-center">
      <p className="font-mono text-[10px] uppercase tracking-[0.22em] text-muted-foreground">
        Audit chain empty
      </p>
      <p className="mt-2 text-xs text-muted-foreground">
        No system events recorded yet for this provider.
      </p>
    </div>
  );
}

/** Events arrive in OccurredAt ASC order from the API; first vs last is enough. */
function spansMultipleDays(events: AuditEventDto[]): boolean {
  if (events.length < 2) return false;
  const first = new Date(events[0].occurredAt);
  const last = new Date(events[events.length - 1].occurredAt);
  return (
    first.getFullYear() !== last.getFullYear() ||
    first.getMonth() !== last.getMonth() ||
    first.getDate() !== last.getDate()
  );
}

type MarkerTone = "score-red" | "score-yellow" | "score-green" | "doc" | "neutral";

const MARKER_BG: Record<MarkerTone, string> = {
  "score-red": "bg-rose-500 ring-rose-500/25",
  "score-yellow": "bg-amber-500 ring-amber-500/25",
  "score-green": "bg-emerald-500 ring-emerald-500/25",
  doc: "bg-zinc-400 ring-zinc-400/20 dark:bg-zinc-500 dark:ring-zinc-500/20",
  neutral:
    "bg-zinc-300 ring-zinc-300/0 dark:bg-zinc-700 dark:ring-zinc-700/0",
};

function markerTone(
  eventType: string,
  parsed: Record<string, unknown> | null,
): MarkerTone {
  if (eventType === "ScoreComputed") {
    const tier = strOrNull(parsed?.["tier"]);
    if (tier === "Red") return "score-red";
    if (tier === "Yellow") return "score-yellow";
    if (tier === "Green") return "score-green";
    return "neutral";
  }
  if (eventType === "DocumentUploaded") return "doc";
  return "neutral";
}

/** A colored dot anchored to the timeline rail on the left of the row. */
function Marker({ tone }: { tone: MarkerTone }) {
  return (
    <span
      aria-hidden
      className={cn(
        "absolute -left-[27px] top-1.5 h-2.5 w-2.5 rounded-full ring-4",
        MARKER_BG[tone],
      )}
    />
  );
}

/**
 * Best-effort one-liner per event type, rendered as a row of monospace
 * key:value chips. Parses the JSONB payload lazily — fields are per-event-
 * type so we can't enumerate every shape here. Unknown event types render
 * no chips (the event-type label alone carries the row).
 */
function PayloadSummary({
  eventType,
  parsed,
}: {
  eventType: string;
  parsed: Record<string, unknown> | null;
}) {
  if (parsed === null) return null;

  switch (eventType) {
    case "ScoreComputed": {
      const score = numOrNull(parsed["score"]);
      const tier = strOrNull(parsed["tier"]);
      const critical = numOrNull(parsed["criticalCount"]);
      const major = numOrNull(parsed["majorCount"]);
      const minor = numOrNull(parsed["minorCount"]);
      if (score === null) return null;
      return (
        <ChipRow>
          <Chip label="score" value={`${score} (${tier ?? "?"})`} strong />
          <Chip
            label="breakdown"
            value={`${critical ?? 0}C · ${major ?? 0}M · ${minor ?? 0}m`}
          />
        </ChipRow>
      );
    }
    case "DocumentUploaded": {
      const docType = strOrNull(parsed["docType"]);
      const conf = numOrNull(parsed["docTypeConfidence"]);
      return (
        <ChipRow>
          <Chip label="docType" value={docType ?? "?"} strong />
          {conf !== null && (
            <Chip label="classifier" value={conf.toFixed(2)} />
          )}
        </ChipRow>
      );
    }
    default:
      return null;
  }
}

function ChipRow({ children }: { children: React.ReactNode }) {
  return (
    <div className="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-1">
      {children}
    </div>
  );
}

function Chip({
  label,
  value,
  strong = false,
}: {
  label: string;
  value: string;
  strong?: boolean;
}) {
  return (
    <span className="inline-flex items-baseline gap-1 font-mono text-[10px] tabular-nums">
      <span className="uppercase tracking-[0.18em] text-muted-foreground/80">
        {label}
      </span>
      <span
        className={cn(
          "text-foreground/85",
          strong && "font-semibold text-foreground",
        )}
      >
        {value}
      </span>
    </span>
  );
}

function tryParse(s: string): Record<string, unknown> | null {
  try {
    const v: unknown = JSON.parse(s);
    return typeof v === "object" && v !== null && !Array.isArray(v)
      ? (v as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

function numOrNull(v: unknown): number | null {
  return typeof v === "number" && Number.isFinite(v) ? v : null;
}

function strOrNull(v: unknown): string | null {
  return typeof v === "string" ? v : null;
}
