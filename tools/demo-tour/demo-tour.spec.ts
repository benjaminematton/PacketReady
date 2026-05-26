import { test, expect } from "@playwright/test";

/**
 * Silent screencap of the PacketReady dashboard demo flow — beats 1–3 of
 * `docs/demo-script.md`. No audio, no narration overlay; the webm captures
 * exactly the viewport.
 *
 * <para>Beats 4 and 5 (intake portal + round-trip) need P5 surfaces that
 * don't exist yet; they're commented out at the bottom for when they land.</para>
 *
 * Prereqs (run before invoking `npm run tour`):
 *   1. docker compose up -d              (Postgres + Langfuse)
 *   2. dotnet run --project apps/api/Api/Api.csproj           (API on :5099)
 *   3. dotnet run --project tools/Seed -- --demo              (3 fixed-Guid providers)
 *   4. npm run dev    (from apps/dashboard/, on :3001)
 *
 * Then:  cd tools/demo-tour && npm run tour
 * Video lands at: tools/demo-tour/test-results/<test-name>/video.webm
 */

const YELLOW_PROVIDER_ID = "22222222-2222-2222-2222-222222222222";

// Hold each frame for a beat so the silent video reads at human speed.
// Tuned against the demo-script timing budget: beat 1 = 30s, beat 2 = 60s,
// beat 3 = 45s. Each `hold(ms)` is "stop here and let the viewer read."
async function hold(ms: number) {
  await new Promise((r) => setTimeout(r, ms));
}

test("dashboard demo tour", async ({ page }) => {
  // ───── Beat 1 — Open on the list, then the yellow provider ─────
  await page.goto("/providers");
  await expect(page.getByRole("heading", { name: "Providers" })).toBeVisible();
  // Let the worst-first list settle so the viewer sees Red, Yellow, Green in order.
  await hold(3_000);

  await page.getByRole("link", { name: /Marcus Yellow/i }).click();

  // ───── Beat 2 — Drill into the top Critical issue ─────
  // Wait for the detail header + score badge to render.
  await expect(page.getByRole("heading", { name: /Marcus Yellow/i })).toBeVisible();
  await expect(page.getByLabel(/Score 62/)).toBeVisible();
  await hold(4_000);

  // The first card in the list is the top Critical. Click opens the side sheet.
  // We target by severity tag text + role to avoid coupling to the wording of
  // the message (which can change with date-based renderings).
  const firstIssueCard = page.getByRole("button").filter({ hasText: /CRITICAL/i }).first();
  await firstIssueCard.click();

  // Sheet should open with Drill-in tab selected.
  await expect(page.getByRole("tab", { name: /Drill-in/i })).toBeVisible();
  // Let the PDF preview render. It's the slowest paint in the panel.
  await hold(6_000);

  // ───── Beat 3 — Switch to the audit-trail tab ─────
  await page.getByRole("tab", { name: /Why we flagged this/i }).click();
  // AuditTrail is a static server-rendered list — renders fast, but hold so
  // the viewer can scan the timeline.
  await hold(6_000);

  // ───── Close on the same shape we opened on ─────
  // Close the sheet (Escape is the canonical Radix dismiss).
  await page.keyboard.press("Escape");
  await hold(1_500);

  // Cap with the score-badge frame held for ~2s before the recorder cuts.
  await expect(page.getByLabel(/Score 62/)).toBeVisible();
  await hold(2_500);
});

// ───── Beats 4 + 5 — intake portal round-trip ─────
//
// Runs only when PORTAL_TOKEN + INTAKE_PROVIDER_ID are set in the env.
// The portal in P5 is "review on-file documents, then submit"; uploads
// happen out-of-band (the agent processes inbound docs into extractions),
// so the tour doesn't drive a file picker — it captures the "provider
// clicks submit, score lands in dashboard" half of the loop.
//
// To record beats 4 + 5:
//   1. Create an intake session + magic link via the API.
//   2. Upload the 4 PDFs from `evals/dataset/packet-001-clean-anderson/`
//      against the resulting provider id.
//   3. Export PORTAL_TOKEN=<token> and INTAKE_PROVIDER_ID=<guid>.
//   4. Run `npm run tour`.
// The README has copy-paste curl recipes for steps 1 + 2.
//
// What's NOT in the tour today (because the UI doesn't exist yet):
//   - "Admin clicks Add Provider" in the dashboard — POST /api/providers
//     exists but there's no admin surface on apps/dashboard.
//   - Showing the magic-link email in MailHog — MailHog isn't in
//     docker-compose.
// Both are flagged in `docs/demo-script.md` Beat 4 as known gaps.

const PORTAL_URL = process.env.PORTAL_URL ?? "http://localhost:3002";
const PORTAL_TOKEN = process.env.PORTAL_TOKEN;
const INTAKE_PROVIDER_ID = process.env.INTAKE_PROVIDER_ID;

test("intake round-trip", async ({ page }) => {
  test.skip(
    !PORTAL_TOKEN || !INTAKE_PROVIDER_ID,
    "Set PORTAL_TOKEN + INTAKE_PROVIDER_ID to record beats 4–5. See README.",
  );

  // ───── Beat 4 — Portal landing, review on-file documents ─────
  await page.goto(`${PORTAL_URL}/portal/${PORTAL_TOKEN}`);
  // Portal renders server-side with the current session state. Let it
  // settle so the viewer sees the document list and the submit button.
  await hold(6_000);

  // Click submit. Per P5 the link is single-use; the action consumes it
  // and the page transitions to /portal/{token}/submitted on success.
  await page.getByRole("button", { name: /submit/i }).click();
  // Hold on the "submitted" terminal screen so the viewer reads the
  // confirmation copy.
  await hold(4_000);

  // ───── Beat 5 — Back to the dashboard, the score has landed ─────
  await page.goto(`/providers/${INTAKE_PROVIDER_ID}`);
  await expect(page.getByRole("heading")).toBeVisible();
  // Hold on the closing frame — same shape as Beat 1's closer.
  await hold(5_000);
});

// Pin the yellow URL constant so anyone reading the spec sees the literal
// Guid the seed --demo flag stages. Keeps the recording deterministic.
test.beforeAll(() => {
  expect(YELLOW_PROVIDER_ID).toBe("22222222-2222-2222-2222-222222222222");
});
