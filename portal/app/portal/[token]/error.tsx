"use client";

// Boundary for unrecoverable failures thrown by the server action — the
// only realistic path here is the .NET side returning 5xx after the
// magic link has already been Consumed (e.g. Hangfire enqueue failed:
// see PortalEndpoints.cs `PortalEnqueueFailed`). At that point the link
// is dead and there is no client-side retry that helps.
//
// `reset` is wired up so a transient transport blip during the GET path
// (before submit) can be retried without reloading the tab.

import { useEffect } from "react";

type Props = {
  error: Error & { digest?: string };
  reset: () => void;
};

export default function PortalError({ error, reset }: Props) {
  useEffect(() => {
    // Server-side stack is in the API log; the digest lets an operator
    // correlate. console.error is enough — no client telemetry in v1.
    console.error("portal error", { digest: error.digest, message: error.message });
  }, [error]);

  return (
    <main className="mx-auto max-w-2xl px-6 py-16">
      <h1 className="text-2xl font-semibold tracking-tight">
        Something went wrong on our side
      </h1>
      <p className="mt-4 text-[color:var(--foreground)]">
        We received your request, but couldn't finish scheduling the next
        step. If you were submitting your intake, please reply to your
        invitation email so we can re-issue a fresh link — your existing
        one may no longer work.
      </p>
      <div className="mt-8 flex gap-3">
        <button
          type="button"
          onClick={reset}
          className="rounded-md border border-neutral-300 px-4 py-2 text-sm font-medium hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-900"
        >
          Try again
        </button>
      </div>
      {error.digest ? (
        <p className="mt-6 font-mono text-xs text-[color:var(--muted)]">
          ref: {error.digest}
        </p>
      ) : null}
    </main>
  );
}
