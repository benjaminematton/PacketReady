# PacketReady — Demo Script

> 5-minute walkthrough, shot-by-shot. Read aloud once before rehearsing.

| | |
|---|---|
| **Owner** | Ben |
| **Length** | 5:00 target, 5:30 hard ceiling |
| **Recording** | Loom at 1440×900 viewport; clean browser profile (no toolbar extensions) |
| **Companion** | [impl/phase-6-demo-polish.md](./impl/phase-6-demo-polish.md), [design.md §13](./design.md) |

---

## Pre-recording checklist

Run through every item once before hitting record. Skipping any one of these is how a demo becomes a re-shoot.

- [ ] `docker compose up -d` (Postgres + Langfuse + MailHog if P5 wired)
- [ ] `dotnet run --project apps/api/Api/Api.csproj` (API on `:5099`)
- [ ] `dotnet run --project tools/Seed -- --demo` (3 fixed-Guid providers staged)
- [ ] `npm run dev` in `apps/dashboard/` (UI on `:3001`)
- [ ] Browser cleared of prior tabs · zoom at 100% · DevTools closed
- [ ] Loom recording window framed on the browser tab (not the whole screen)
- [ ] Quiet room · phone silenced · second monitor closed if it shows notifications
- [ ] Read this script aloud once before the take. Awkward phrasing surfaces here, not on take 4.

---

## The arc in one sentence

> "The intake collects the data; the score is what we deliver. We open on the score, drill into one Critical issue with the source PDF and the audit chain, then rewind to show the intake produced it — closing back on the same score."

Three minutes of "this is what's hard about credentialing"; two minutes of "this is how PacketReady solves it."

---

## Beat 1 — Open on the score (0:00–0:30)

### Setup
```
URL:    http://localhost:3001/providers
Click:  the second row — "Dr. Marcus Yellow"
Lands:  http://localhost:3001/providers/22222222-2222-2222-2222-222222222222
```

### Say

> "Every credentialing packet sent to a payer either gets approved or sent back. A bounced packet restarts a 90-to-120-day cycle and costs a small practice six to eight thousand dollars per provider per month in lost billing. PacketReady scores a packet before it goes out.
>
> This provider scores 62 of 100 — yellow. One Critical, one Major, one Minor. The score is the headline; the issues below tell you what to fix."

### Frame
- Header score badge prominent — yellow pill, "62" in tabular nums.
- Breakdown bar visible: `[1 Critical] [1 Major] [1 Minor]`.
- Three issue cards stacked below, sorted Critical first.

### Time check
- Should land here at **0:30**.
- If you're past 0:35, you talked too long. Cut "every credentialing packet sent to a payer."

---

## Beat 2 — Drill into the Critical (0:30–1:30)

### Setup
```
Click:  the top issue card — "License expired on 2025-10-15"
Sheet:  opens from the right
Tab:    "Drill-in" is selected by default
```

### Say (slower than beat 1 — this is where the system *shows* something)

> "Click any issue, you get the source. This is the license PDF, page 1 — the highlighted region is the literal extracted-value the validator reasoned over. Same value as the message above. The dashboard isn't summarizing the document; it's citing it.
>
> Two values matter here: the expiry date we pulled, and where on the page we pulled it from. If a reviewer asks 'where did you get that?' we click here, and the answer is the rectangle on the PDF."

### Frame
- Sheet panel covers right ~40% of the screen.
- Citation block: validator name in mono uppercase, extracted value verbatim, then the embedded PDF page with the amber bbox highlight.
- The page renders below the citation text within the sheet — don't scroll past it.

### Time check
- Around **1:30** at the end of "rectangle on the PDF."
- The PDF preview may take a beat to render on first open — wait for it, don't talk through it.

---

## Beat 3 — Show the audit trail (1:30–2:15)

### Setup
```
Click:  the "Why we flagged this" tab inside the open sheet
Render: AuditTrail timeline replaces the Drill-in content
```

### Say

> "Behind every issue: a Haiku classification, a Sonnet extraction, a validator invocation, and the score synthesis. Every step has cost, latency, and an audit row. One Critical issue runs about two cents end-to-end.
>
> Nothing in this column is a heuristic. Each row is one event in the audit log — Postgres, append-only, enforced by a trigger. If a customer asks 'how did this score change between Monday and Friday' we don't guess; we read it off."

### Frame
- Vertical timeline, mono font, time stamps right-aligned.
- Each row: event type, payload summary (model, cost, counts), wall-clock time.
- At least 4 rows visible — `DocumentUploaded` × N, `ScoreComputed` at the bottom.

### Time check
- Land at **2:15**.
- Resist the urge to read every audit row aloud. Three rows mentioned by category, then move on.

---

## Beat 4 — Rewind to intake (2:15–3:45)

This is the longest beat — the intake flow has the most moving parts. Slow down here.

### Setup
```
Click:  "All providers" link at the top of the detail page
Click:  "+ Add Provider" button on the list page (P5 surface)
Fill:   minimal form (name, email — use a real-looking demo email)
Submit: triggers magic-link email + outbox dispatch
Switch: to MailHog tab (http://localhost:8025) showing the staged email
Click:  the magic link in the email body (opens new tab)
Land:   intake portal at /portal/{magicLinkId}
Upload: drag 4 PDFs from evals/dataset/packet-001-clean-anderson/
Answer: 2 short questions
Submit: triggers the first agent turn
```

### Say

> "Other side of the system. Admin adds a new provider — one name, one email. The agent sends the provider one email with a magic link. The provider clicks through, uploads what they have on hand, answers a couple of context questions — and submits.
>
> No 47-field form. No 12 follow-up reminders. The agent decides what's missing and asks for exactly that, in one email."

### Frame
- MailHog and the intake portal each take one full beat of screen time. Don't switch tabs faster than the viewer can read.
- When you upload, drop all 4 PDFs at once — don't add them one at a time.
- The 2 questions answer quickly — pre-fill the answers if Loom supports paste-during-recording, or rehearse the typing.

### Time check
- Submit at **3:45**.
- If you're at 3:50 because uploads were slow, that's OK — the audience expects file ops to take a beat.

---

## Beat 5 — Round trip (3:45–5:00)

### Setup
```
Switch:  to the Langfuse tab (http://localhost:3000)
Show:    the trace tree for the second turn — classify × 4, extract × 4, validators, score
Switch:  back to the dashboard list (http://localhost:3001/providers)
Wait:    for the new provider to appear at the top (created_at desc)
Click:   the new provider
Frame:   the same screen shape we opened on — header score badge, issues list
```

### Say

> "Second turn fires. The agent extracts every document, runs every validator, computes the score — visible here as one Langfuse trace.
>
> And we end on the screen we opened with. The intake produced the data the score reads. The system is the same system, end to end."

### Frame
- Langfuse trace tree should show the classify/extract/validator/score chain — don't dwell, just *show* it for ~5 seconds.
- The closing frame is the new provider's detail page — same shape as the opener (Beat 1), different score.
- End on a held shot of the score badge. 2 seconds of silence before cutting.

### Time check
- Cut at **5:00**.
- Hard ceiling at 5:30 — past that, edit ruthlessly on the next take.

---

## What NOT to say

Stuff that creeps into demos and shouldn't.

- ❌ "It's still rough" / "we're still polishing" — invites the reviewer to look for rough spots.
- ❌ "Eventually we'll" / "the plan is to" — the demo is the artifact; future work belongs in conversation, not the recording.
- ❌ Apologies for loading states, even on slow PDF renders. Wait. Silence reads as confidence; nervous chatter doesn't.
- ❌ Naming Atano. The demo is product-first; the audience can map it themselves.
- ❌ Mentioning the labeler-bias caveat in the video. It's in the README — that's where it belongs.

---

## What to say if asked

These come up in conversation after the video, not in the recording itself.

| Question | Answer |
|---|---|
| Where are the accuracy numbers? | `README.md` — published from `evals/results/baseline.json`. 50 packets, 4 doc types, per-field accuracy + conflict precision/recall + tier agreement. |
| How does it handle real provider data? | Synthetic only in this build. Real PHI needs HIPAA controls and a BAA; out of scope for the demo. The eval set uses NPPES-sampled distributions but generated PDFs. |
| What does it cost to run a packet? | About six dollars to evaluate the whole 50-packet set end-to-end. Per-provider cost is under fifty cents — Haiku for classification, Sonnet for extraction and the two LLM validators. |
| Why no CAQH integration? | The customers we'd target (small practices, RCMs) frequently onboard providers who aren't in CAQH yet. The intake-first design is shaped for them. CAQH-fallback is a documented contract; live integration is post-launch. |
| Bbox accuracy? | Sonnet self-reports field locations. We don't measure bbox accuracy formally in P4 — the demo eyeballs it; a future eval slice would label ground-truth bboxes on a small subset. |

---

## Re-shoot triggers

After watching back, **redo the whole take** if any of these are true:

- A cut feels like a cut — visible loading spinners, mid-sentence tab switches, tabs opening past the viewport.
- You apologized or hedged on camera.
- Total length over 5:30.
- The closing frame is not the score badge.
- The Langfuse trace tab obviously wasn't ready when you switched to it (collapsed nodes, "loading…", connection error).
- Your voice noticeably warmed up by minute 2 — re-record with warmer voice through the whole take.

Otherwise: **fix the top three issues on a single re-shoot, then stop.** Past the third take, you're polishing voice instead of system; ship it.

---

## Recording-day timing

Allow 90 minutes total:

- 15 min — final pre-recording checklist run
- 15 min — silent dry run (no narration; just clicks, to lock the click sequence in your hands)
- 5 min × 3 takes = 15 min recording
- 15 min — watching back · marking issues
- 15 min — one polish pass on top issues
- 5 min × 2 takes = 10 min re-record
- 5 min — pick the take, download MP4 backup, update Loom share link in README

If you're past 90 minutes, the demo isn't broken — your standards are. Pick the least-bad take and ship.

---

## After the recording

1. Upload to Loom (or paste MP4 link if self-hosting).
2. Download MP4 backup to `~/Documents/packetready-demo-YYYY-MM-DD.mp4`.
3. Update `README.md` hero — embed Loom, link MP4 as fallback.
4. Walk the [P6 DoD checklist](./impl/phase-6-demo-polish.md#definition-of-done) — every box.
5. Write `docs/closing-notes.md` (per the P6 doc's close-out hook).
