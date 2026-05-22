import type { Tier } from "@/lib/types";
import { cn } from "@/lib/utils";

const TIER_BG = {
  Green: "bg-emerald-600",
  Yellow: "bg-amber-500",
  Red: "bg-rose-600",
} satisfies Record<Tier, string>;

/**
 * Tier-colored score pill. Renders the numeric score with a tier-driven color
 * background. `null` score (provider exists but never scored) shows a neutral
 * dash — the side-panel will explain.
 *
 * Color choice: solid background with white text, rounded full. Bright enough
 * to read at a glance in a list view, restrained enough that it doesn't shout
 * in the detail header. P1 doesn't need a small/large variant; the same size
 * works both contexts.
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
          "inline-flex h-7 min-w-14 items-center justify-center rounded-full bg-zinc-200 px-3 text-sm font-medium text-zinc-500 dark:bg-zinc-800 dark:text-zinc-400",
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
        "inline-flex h-7 min-w-14 items-center justify-center rounded-full px-3 text-sm font-semibold tabular-nums text-white",
        TIER_BG[tier],
        className,
      )}
      aria-label={`Score ${score} of 100, tier ${tier}`}
    >
      {score}
    </span>
  );
}
