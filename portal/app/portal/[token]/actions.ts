"use server";

import { redirect } from "next/navigation";
import { submitPortal } from "@/lib/api";

/**
 * Server action backing the portal submit button. The .NET API
 * consumes the magic-link and enqueues the agent turn; this action
 * just forwards + redirects.
 *
 * On 410 (expired / consumed / malformed) we bounce back to the same
 * route — the server component re-fetches the now-invalid state and
 * renders the appropriate banner. On any other transport error we
 * surface as a Next.js error boundary (no explicit error UI in v1).
 */
export async function submitAction(token: string): Promise<void> {
  const result = await submitPortal(token);

  if ("kind" in result) {
    if (result.kind === "magic_link_invalid") {
      // Same-route redirect; the GET re-fetches and renders the
      // "link no longer valid" branch with the canonical reason.
      redirect(`/portal/${encodeURIComponent(token)}`);
    }
    // Transport / 5xx — throw so Next.js's nearest error.tsx (or the
    // default error page) renders. Operators see the underlying status
    // in the log.
    throw new Error(
      `Submit failed: status=${result.status}, detail=${result.detail.slice(0, 200)}`,
    );
  }

  redirect(`/portal/${encodeURIComponent(token)}/submitted`);
}
