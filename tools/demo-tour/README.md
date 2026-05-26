# demo-tour

Silent Playwright screencap of the PacketReady demo flow. Produces a `.webm` of beats 1–5 from [docs/demo-script.md](../../docs/demo-script.md). No audio, no narration overlay — exactly the viewport.

## What it captures

| Beat | Content | Requires |
|---|---|---|
| 1 (~30s) | `/providers` list, sorted worst-first; click into Dr. Marcus Yellow | seed `--demo` |
| 2 (~60s) | Detail page (header, score badge, breakdown); open the top Critical issue's Sheet; PDF preview with bbox highlight | seed `--demo` |
| 3 (~45s) | Switch to "Why we flagged this" tab; audit chain visible; close the Sheet on the score badge | seed `--demo` |
| 4 (~30s) | Portal landing at `:3002/portal/{token}` — review on-file documents, click Submit | `PORTAL_TOKEN` env + intake set up (see below) |
| 5 (~30s) | Back to dashboard, new provider's score has landed | `INTAKE_PROVIDER_ID` env |

Total runtime: ~3:00 with beats 4+5, ~2:00 without. Beats 4+5 auto-skip when the env vars aren't set.

**Known gaps in the recorded video** (captured as such in `docs/demo-script.md` Beat 4):
- No admin-side "Add Provider" UI yet — `POST /api/providers` exists, but no button on `apps/dashboard/`.
- No MailHog in `docker-compose.yml`, so the magic-link email isn't shown on screen. The video jumps from "imagine the email" → the portal landing page.

## Prereqs (run in order, in separate terminals)

```bash
# 1. Postgres + Langfuse
docker compose up -d

# 2. API on :5099
dotnet run --project apps/api/Api/Api.csproj

# 3. Seed the 3 fixed-Guid demo providers
dotnet run --project tools/Seed -- --demo

# 4. Dashboard on :3001
cd apps/dashboard && npm run dev

# 5. Portal on :3002 (only needed for beats 4+5)
cd portal && npm run dev
```

Don't run the tour against the regular seed — the spec assumes the yellow provider lives at the fixed Guid `22222222-2222-2222-2222-222222222222`, which only the `--demo` flag stages.

## (Optional) Set up beats 4 + 5

Run this once before recording to provision a fresh intake session + portal token. Requires `jq`.

```bash
# A. Create a provider via the eval-orchestrator endpoint
PROV_ID=$(curl -s -X POST http://localhost:5099/api/providers \
  -H 'content-type: application/json' \
  -d '{"identity":{"fullName":"Demo Provider","npi":"1234567890","credentialingState":"NY","dateOfBirth":"1980-01-01"}}' \
  | jq -r .id)
echo "provider: $PROV_ID"

# B. Upload the 4 PDFs from the clean Anderson packet
for doc in license dea board-cert malpractice; do
  curl -sf -X POST "http://localhost:5099/api/providers/${PROV_ID}/documents" \
    -F "file=@evals/dataset/packet-001-clean-anderson/${doc}.pdf" \
    -o /dev/null && echo "uploaded ${doc}.pdf"
done

# C. Wait ~30s for the IntakeTurnJob to process extractions before opening
#    the portal — `canSubmit` flips to true only after the agent decides
#    we have enough on file.
sleep 30

# D. Start the intake and pluck the magic-link token
TOKEN=$(curl -s -X POST http://localhost:5099/api/intakes \
  -H 'content-type: application/json' \
  -d "{\"providerId\":\"$PROV_ID\",\"email\":\"demo@example.local\"}" \
  | jq -r .token)
echo "token: $TOKEN"

# E. Export for the tour and record
export PORTAL_TOKEN="$TOKEN"
export INTAKE_PROVIDER_ID="$PROV_ID"
cd tools/demo-tour && npm run tour
```

Without steps A–E, the tour still records beats 1–3 cleanly; the intake test auto-skips with a message.

## Record

```bash
cd tools/demo-tour
npm install              # first time only
npx playwright install chromium   # first time only
npm run tour
```

Output: `tools/demo-tour/test-results/<test-name>/video.webm`.

## Tweak the pace

Each "frame hold" lives in the spec as a `hold(ms)` call. Default cadence:
- 3s on the list view (let the worst-first ordering settle)
- 4s on the detail view (let the score badge + breakdown read)
- 6s on the open Sheet with PDF preview (slowest paint; needs the most time)
- 6s on the audit-trail tab
- 2.5s on the closing frame
- 6s on the portal landing (beat 4)
- 4s on the "submitted" terminal screen
- 5s on the new-provider closing frame (beat 5)

Speed up by halving the `hold` values; slow down by doubling. The viewport size and slowMo step are in `playwright.config.ts`.

## Convert to MP4 for sharing

Loom doesn't accept `.webm` directly. ffmpeg one-liner:

```bash
ffmpeg -i test-results/*/video.webm -c:v libx264 -pix_fmt yuv420p demo.mp4
```

The README's hero embed should link the MP4, not the webm — broader compatibility.

## Why headless + Playwright instead of QuickTime

- **Deterministic.** Same starting state + same script + same viewport = same video. Re-record by re-running the script; no human framing variance.
- **No browser chrome leaks.** Playwright captures the viewport, not the surrounding window. No address bar, no bookmarks toolbar, no notification popups mid-recording.
- **Re-runnable as the dashboard evolves.** When P5 ships the intake portal, append a second `test()` and the tour grows without touching the existing beats.
- **No audio.** That's the trade — you wanted no audio anyway.
