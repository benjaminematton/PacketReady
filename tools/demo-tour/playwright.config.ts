import { defineConfig, devices } from "@playwright/test";

/**
 * Demo-tour config. Single chromium project, video always on, fixed viewport
 * so the resulting webm dimensions are stable across machines.
 *
 * Why headless: the captured video is the viewport contents — browser chrome
 * (URL bar, tabs) doesn't appear in either mode, and headless is faster to
 * boot. If the demo ever needs a "real browser" feel (cursor, chrome), flip
 * `headless: false`.
 *
 * SlowMo paces each action so the silent video reads at human speed without
 * per-step `waitForTimeout` calls cluttering the spec.
 */
export default defineConfig({
  testDir: ".",
  // Single retry would mean a flaky run produces two videos; we want exactly one.
  retries: 0,
  // No parallelism — one video at a time, deterministic order.
  fullyParallel: false,
  workers: 1,
  use: {
    baseURL: process.env.DASHBOARD_URL ?? "http://localhost:3001",
    viewport: { width: 1440, height: 900 },
    video: {
      mode: "on",
      size: { width: 1440, height: 900 },
    },
    // Each click/fill animates over ~250ms so the silent video isn't a blur.
    launchOptions: {
      slowMo: 250,
    },
    // Be patient on PDF preview / API roundtrips.
    actionTimeout: 15_000,
    navigationTimeout: 15_000,
  },
  outputDir: "test-results",
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
