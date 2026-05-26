// PacketReady demo recorder. Drives the dashboard with Playwright, captures
// via Playwright's video API, then ffmpeg converts the webm to a
// palette-optimised GIF that lands directly at docs/assets/demo.gif.
//
// Pattern lifted from venue-concierge/scripts/record-hero.ts. Standalone
// script over the test-runner so the recording flow isn't intertwined
// with expect() assertions and there's no test-results/ intermediate dir.
//
// Run (from tools/demo-tour/):  npm run tour
// Output:                       docs/assets/demo.gif (relative to repo root)
//
// Prereqs (run before invoking):
//   1. docker compose up -d                                       (Postgres)
//   2. set -a && source .env && set +a
//   3. dotnet run --project tools/Seed -- --demo                  (seed 3 fixtures)
//   4. ASPNETCORE_URLS=http://localhost:5099 dotnet run \
//        --project apps/api/Api/Api.csproj --launch-profile http  (API)
//   5. cd apps/dashboard && API_BASE_URL=http://localhost:5066 \
//        NEXT_PUBLIC_API_BASE_URL=http://localhost:5066 npm run dev

import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { chromium } from "playwright";

const DASHBOARD_URL = process.env.DASHBOARD_URL ?? "http://localhost:3001";
// Marcus Yellow is the canonical demo provider — seeded at a fixed Guid by
// `tools/Seed -- --demo` so this URL never moves between rehearsals.
const YELLOW_PROVIDER_ID = "22222222-2222-2222-2222-222222222222";

// Frame just slightly wider than the dashboard's max-w-3xl content (~768 px
// + px-6 gutters) so the recording reads like a real laptop browser window
// rather than a sea of whitespace. 1280×720 matches venue-concierge's
// hero.gif sizing and keeps text legible after the 860-wide GIF downscale.
const VIEWPORT = { width: 1280, height: 720 };

const REPO_ROOT = resolve(__dirname, "..", "..");
const VIDEO_DIR = join(__dirname, "video-tmp");
const OUTPUT_GIF = join(REPO_ROOT, "docs", "assets", "demo.gif");

// Per-beat hold lengths, tuned so the silent recording reads at human speed.
// Total ≈ 21s of demo + the ffmpeg 0.9s head-trim below.
const HOLD = {
  list: 3_000,        // beat 1 — read the worst-first list
  detail: 3_500,      // beat 2 — read the score + issue ladder
  pdf: 5_500,         // beat 3 — wait for the PDF preview to fully render
  audit: 5_000,       // beat 4 — read the audit timeline
  closer: 2_500,      // beat 5 — finish on the score badge
};

async function record(): Promise<string> {
  if (existsSync(VIDEO_DIR)) rmSync(VIDEO_DIR, { recursive: true, force: true });
  mkdirSync(VIDEO_DIR, { recursive: true });

  const browser = await chromium.launch();
  const context = await browser.newContext({
    viewport: VIEWPORT,
    recordVideo: { dir: VIDEO_DIR, size: VIEWPORT },
    // Records at 2x then downscales in ffmpeg — keeps text crisp in the
    // final GIF instead of the soft blur you get from native 1x.
    deviceScaleFactor: 2,
  });
  const page = await context.newPage();

  // ── Beat 1 — Open on the worst-first provider list ──
  console.log(`navigating to ${DASHBOARD_URL}/providers…`);
  await page.goto(`${DASHBOARD_URL}/providers`, { waitUntil: "networkidle" });
  await page.getByRole("heading", { name: "Providers" }).waitFor();
  await page.waitForTimeout(HOLD.list);

  // ── Beat 2 — Drill into Marcus Yellow ──
  await page.getByRole("link", { name: /Marcus Yellow/i }).click();
  await page.getByRole("heading", { name: /Marcus Yellow/i }).waitFor();
  // Case-insensitive — copy iterations have swung between "Score 62" and
  // "Readiness score 62 of 100, tier Yellow"; the 62 anchors the selector.
  await page.getByLabel(/score 62/i).waitFor();
  await page.waitForTimeout(HOLD.detail);

  // ── Beat 3 — Open the top Critical issue's drill-in sheet ──
  // Target by severity badge to avoid coupling to issue-message wording.
  const firstCritical = page
    .getByRole("button")
    .filter({ hasText: /CRITICAL/i })
    .first();
  await firstCritical.click();
  await page.getByRole("tab", { name: /Drill-in/i }).waitFor();
  // PDF preview is the slowest paint in the sheet — give it the full hold.
  await page.waitForTimeout(HOLD.pdf);

  // ── Beat 4 — Switch to the audit-trail tab ──
  await page.getByRole("tab", { name: /Why we flagged this/i }).click();
  await page.waitForTimeout(HOLD.audit);

  // ── Beat 5 — Close on the same shape we opened on ──
  await page.keyboard.press("Escape");
  await page.getByLabel(/score 62/i).waitFor();
  await page.waitForTimeout(HOLD.closer);

  await context.close();
  await browser.close();

  const files = readdirSync(VIDEO_DIR).filter((f) => f.endsWith(".webm"));
  if (files.length === 0) throw new Error("no video file produced");
  return join(VIDEO_DIR, files[0]);
}

function run(cmd: string, args: string[]): Promise<void> {
  return new Promise((resolve, reject) => {
    const p = spawn(cmd, args, { stdio: "inherit" });
    p.on("close", (code) =>
      code === 0 ? resolve() : reject(new Error(`${cmd} exited ${code}`)),
    );
  });
}

async function convertToGif(webm: string) {
  // Palette generation tuned for a mostly-static UI surface:
  //   - fps=12 keeps it smooth enough for cursor + sheet open animations
  //     without ballooning frame count
  //   - scale=860 matches venue-concierge's frame width (renders at a
  //     comfortable size in GitHub README)
  //   - stats_mode=diff focuses palette on changing regions — most pixels
  //     are background-static and don't need quantization effort
  //   - paletteuse diff_mode=rectangle avoids dithering noise on still
  //     regions between frames (smaller file, no visible quality loss)
  const filters =
    "fps=12,scale=860:-1:flags=lanczos,split[a][b];" +
    "[a]palettegen=max_colors=128:stats_mode=diff[p];" +
    "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";

  console.log("running ffmpeg…");
  // `-ss 0.9 -i` seeks 900 ms into the input — Playwright begins recording
  // at context creation, so the first ~1s is the blank loading page. Skip
  // it so the GIF opens directly on the rendered provider list.
  await run("ffmpeg", [
    "-y",
    "-ss", "0.9",
    "-i", webm,
    "-vf", filters,
    "-loop", "0",
    OUTPUT_GIF,
  ]);

  rmSync(VIDEO_DIR, { recursive: true, force: true });
}

async function main() {
  const webm = await record();
  console.log(`recorded: ${webm}`);
  await convertToGif(webm);
  console.log(`gif written to ${OUTPUT_GIF}`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
