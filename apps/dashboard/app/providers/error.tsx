"use client";

import { useEffect } from "react";
import Link from "next/link";
import { Button } from "@/components/ui/button";

/**
 * Error boundary for the `/providers` route segment — catches anything thrown
 * by the list or detail server components that isn't a `notFound()` (which
 * routes to the segment's `not-found.tsx` instead).
 *
 * <para>Examples it catches: API down (Network error from the fetch wrapper),
 * malformed JSON, an audit-fetch failure that escapes the try/catch in the
 * detail page. Without this, Next falls back to its default error UI, which
 * is hostile mid-demo.</para>
 *
 * <para>Client component because error boundaries require it. The `reset`
 * callback re-renders the segment from scratch — the recovery path the user
 * actually wants ("the API came back; try again").</para>
 */
export default function ProvidersError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    // Surface to the browser console in dev; production deploys can hook this
    // to a real logger later. Keeping it side-effect-only so a CSP that blocks
    // inline scripts doesn't bite.
    console.error("Providers route error:", error);
  }, [error]);

  const isDev = process.env.NODE_ENV !== "production";

  return (
    <main className="mx-auto max-w-3xl px-6 py-10">
      <div className="rounded-lg border border-rose-200 bg-rose-50 px-5 py-4 dark:border-rose-900 dark:bg-rose-950">
        <p className="text-sm font-medium text-rose-900 dark:text-rose-200">
          Something went wrong loading providers.
        </p>
        <p className="mt-1 text-xs text-rose-800 dark:text-rose-300">
          {error.message || "Unknown error."}
        </p>
        {isDev && error.digest && (
          <p className="mt-2 font-mono text-[10px] text-rose-700/80 dark:text-rose-400/70">
            digest {error.digest}
          </p>
        )}
        <div className="mt-4 flex flex-wrap items-center gap-3">
          <Button onClick={reset} size="sm" variant="default">
            Try again
          </Button>
          <Link
            href="/providers"
            className="text-xs text-rose-700 underline hover:text-rose-900 dark:text-rose-300 dark:hover:text-rose-100"
          >
            Back to providers
          </Link>
        </div>
        {isDev && (
          <p className="mt-4 text-[11px] text-rose-700/80 dark:text-rose-400/70">
            Is the API running? <code className="font-mono">dotnet run --project apps/api/Api/Api.csproj</code>
          </p>
        )}
      </div>
    </main>
  );
}
