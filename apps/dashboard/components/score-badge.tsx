import type { Tier } from "@/lib/types";
import { cn } from "@/lib/utils";

const TIER_BG = {
  Green: "bg-emerald-600 ring-emerald-700/40",
  Yellow: "bg-amber-500 ring-amber-600/40",
  Red: "bg-rose-600 ring-rose-700/40",
} satisfies Record<Tier, string>;

/**
 * Tier-colored score pill. Renders the numeric score with a tier-driven color
 * background. `null` score (provider exists but never scored) shows a neutral
 * dash — the side-panel will explain.
 *
 * Typography choice: Geist Mono, tabular-nums, semibold, tight tracking. Reads
 * as instrument output (a measured value) rather than a brand badge. Inner
 * ring deepens the tier color without resorting to a heavier shadow — the
 * pill stays legible at small sizes (list view) and dominant at large ones
 * (detail header override) without restyling.
 */
export function ScoreBadge({
  score,
  tier,
  className,
}: {
  score: number | null;
  tier: Tier | null;
  className?: string;
}) {
  if (score === null || tier === null) {
    return (
      <span
        className={cn(
          "inline-flex h-7 min-w-[3.5rem] items-center justify-center rounded-full bg-zinc-100 px-3 font-mono text-sm font-medium tabular-nums text-zinc-500 ring-1 ring-inset ring-zinc-200 dark:bg-zinc-900 dark:text-zinc-500 dark:ring-zinc-800",
          className,
        )}
        aria-label="No score computed"
      >
        —
      </span>
    );
  }

  return (
    <span
      className={cn(
        "inline-flex h-7 min-w-[3.5rem] items-center justify-center rounded-full px-3 font-mono text-sm font-semibold tabular-nums tracking-tight text-white ring-1 ring-inset",
        TIER_BG[tier],
        className,
      )}
      aria-label={`Score ${score} of 100, tier ${tier}`}
    >
      {score}
    </span>
  );
}
