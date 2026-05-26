import type { AuditEventDto } from "@/lib/types";

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
 * model + cost when the payload carries them (extraction / score-synth),
 * and the wall-clock time. Per-row Langfuse deep-link is a P6.5 follow-on —
 * the current `LangfuseTelemetry.OtlpEndpointSuffixes` machinery is on the
 * backend, and surfacing per-trace URLs requires a small endpoint addition
 * we haven't blocked the demo on.</para>
 */
export function AuditTrail({ events }: { events: AuditEventDto[] }) {
  if (events.length === 0) {
    return (
      <p className="text-xs italic text-muted-foreground">
        No audit events recorded yet for this provider.
      </p>
    );
  }

  const formatter = spansMultipleDays(events) ? DATETIME_FORMAT : TIME_FORMAT;

  return (
    <ol className="space-y-1.5">
      {events.map((e, idx) => (
        <li key={e.id} className="flex items-start gap-3">
          <Marker isLast={idx === events.length - 1} />
          <div className="min-w-0 flex-1 pb-1">
            <div className="flex items-baseline justify-between gap-2">
              <p className="font-mono text-xs font-medium text-foreground">
                {e.eventType}
              </p>
              <p className="shrink-0 font-mono text-[10px] tabular-nums text-muted-foreground">
                {formatter.format(new Date(e.occurredAt))}
              </p>
            </div>
            <PayloadSummary eventType={e.eventType} payload={e.payload} />
          </div>
        </li>
      ))}
    </ol>
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

/** A small dot + vertical connector. Last row drops the connector. */
function Marker({ isLast }: { isLast: boolean }) {
  return (
    <div className="relative flex shrink-0 flex-col items-center pt-1.5">
      <span
        aria-hidden
        className="h-1.5 w-1.5 rounded-full bg-muted-foreground/60"
      />
      {!isLast && (
        <span
          aria-hidden
          className="absolute top-2.5 h-full w-px bg-border"
        />
      )}
    </div>
  );
}

/**
 * Best-effort one-liner per event type. Parses the JSONB payload lazily —
 * fields are per-event-type so we can't enumerate every shape here. Unknown
 * event types render no summary (the event-type label alone is enough).
 */
function PayloadSummary({
  eventType,
  payload,
}: {
  eventType: string;
  payload: string;
}) {
  const parsed = tryParse(payload);
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
        <p className="mt-0.5 text-[11px] text-muted-foreground">
          score {score} ({tier ?? "?"}) ·{" "}
          <span className="tabular-nums">
            {critical ?? 0}C / {major ?? 0}M / {minor ?? 0}Min
          </span>
        </p>
      );
    }
    case "DocumentUploaded": {
      const docType = strOrNull(parsed["docType"]);
      const conf = numOrNull(parsed["docTypeConfidence"]);
      return (
        <p className="mt-0.5 text-[11px] text-muted-foreground">
          {docType ?? "doc"} (classifier conf{" "}
          <span className="tabular-nums">{conf?.toFixed(2) ?? "?"}</span>)
        </p>
      );
    }
    default:
      return null;
  }
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
