# Phase 6 — Demo Polish

> The 5-minute demo in [design.md §13](../design.md) records cleanly on first take. P6 doesn't move the central claim — it makes everything already built legible at demo pace.

| | |
|---|---|
| **Parent** | [build-plan.md](../build-plan.md) — Phase 6 row |
| **Goal** | A recorded demo. A README hero. Both shippable as one artifact. |
| **Status** | Not started |
| **Data** | 3 pre-staged providers · same synthetic dataset as P2–P5 · no PHI |
| **Depends on** | [Phase 5](./phase-5-intake-agent.md) — full lifecycle wired |
| **Style** | [../style.md](../style.md) |

---

## Definition of done

- [ ] **5-minute demo recorded as a Loom (or equivalent).** Watched back once at full speed and the cuts don't feel like cuts. Hosted at a stable URL that the README links.
- [ ] **3 demo providers pre-staged** at fixed Guids by an extension of the P1 seed CLI (`tools/Seed/Program.cs --demo`). One green (score ≥ 85), one yellow (62), one red (34). URLs in the demo script don't change between rehearsals.
- [ ] **Dashboard drill-in renders the source PDF with the bbox highlighted** on every Issue with a `Citation.Bbox`. The P3 field-location pipeline finally has a UI consumer.
- [ ] **Audit-log side panel** opens from any Issue card. Surfaces the classifier call (model + cost) → extraction call (model + cost) → validator invocation → score synthesis, in time order. One line per `AuditEvent` in the chain; per-step Langfuse deep-link on each row.
- [ ] **README hero section** rewritten: one-paragraph TL;DR + the P4 accuracy table + the [Appendix A](../design.md) competitor comparison + the demo Loom + the bias caveat. Above-the-fold readable on a 13" screen.
- [ ] **`docs/demo-script.md`** committed: shot-by-shot walkthrough. Each beat names the URL, the click, and the line to say. Rehearse-able solo.
- [ ] **Loading / empty / error states** are clean at every screen the demo touches — list, detail, side panel, intake portal upload. Nothing screams "I forgot this state" mid-recording.
- [ ] **Rehearsed 3×** with a stopwatch. Each run ≤ 5:30. The shape of the demo is fixed; only the words drift.

All eight boxes check → Phase 6 closes. PacketReady ships as a sendable artifact.

---

## Stack additions

| Layer | Addition | Why |
|---|---|---|
| Frontend | `react-pdf` (or `pdfjs-dist`) | PDF preview + page-relative bbox overlay. shadcn doesn't ship a PDF primitive. |
| Frontend | (no other new deps) | shadcn + Tailwind defaults carry the rest. |
| Tooling | Loom (or QuickTime + manual upload) | Demo recording host. Loom's per-section chapters are useful; QuickTime + a public S3 link works too. |

No backend changes. **If you're touching the API in P6, you're scope-creeping.**

---

## Decisions baked in

| Decision | Choice | Why locked here |
|---|---|---|
| PDF renderer | `react-pdf` (Mozilla `pdfjs-dist` wrapper) | Server-side blob URL → client-side render. Avoids streaming the PDF through the API again. |
| Bbox-overlay coordinate space | Normalized 0..1 on each axis (matches `BoundingBox` from P1) × the rendered page size at display time | One conversion at render time; no aspect-ratio surprises across PDF page sizes. |
| Demo recording length | 5:00 target, 5:30 hard ceiling | The design-doc demo script is 5 minutes by construction. Anything over feels like padding. |
| Loom vs MP4 | Loom for the share link; download an MP4 backup the day of recording | Loom can change pricing or take the video down; MP4 is the durable copy. |
| Demo data | Three providers from P5's intake-agent output, NOT P2 fixtures | The fixtures are the score-from-clean-input demo; P6 demo shows the *full lifecycle*. Stable Guids are seeded explicitly. |
| Out-of-scope discipline | Anything that isn't shot in the 5-minute video doesn't get polish | Stops "while we're in there" creep cold. If it's not on screen during the demo, it stays unpolished. |

---

## Project layout deltas

```
PacketReady/
├── apps/
│   └── dashboard/
│       ├── components/
│       │   ├── pdf-preview.tsx                  NEW — react-pdf wrapper + bbox overlay
│       │   ├── audit-trail.tsx                  NEW — chronological log + Langfuse deep-links
│       │   └── issue-card.tsx                   EXTEND — opens both side-panel sheets
│       ├── app/providers/[id]/
│       │   ├── page.tsx                         EXTEND — wires the new components
│       │   └── (no other route changes)
│       └── lib/
│           └── pdf.ts                           NEW — bbox-overlay math helpers
├── docs/
│   ├── demo-script.md                           NEW — shot-by-shot
│   └── impl/phase-6-demo-polish.md              this doc
├── tools/
│   └── Seed/Program.cs                          EXTEND — --demo flag stages 3 fixed-Guid providers
├── README.md                                    REWRITE — hero section
└── (no backend file changes expected)
```

---

## File-by-file

### `apps/dashboard/components/pdf-preview.tsx`

Client component. Takes a `documentId`, fetches the PDF blob URL from the API, renders the requested `page` via `react-pdf`, draws `bbox` (if present) as a semi-transparent rectangle in normalized coordinates scaled to the rendered page dimensions.

```tsx
"use client";
type Props = {
  documentId: string;
  page: number;
  bbox: BoundingBox | null;   // { x1, y1, x2, y2 } in 0..1
};

export function PdfPreview({ documentId, page, bbox }: Props) {
  // 1. fetch /api/documents/{id}/blob
  // 2. <Document file={url}><Page pageNumber={page} onRenderSuccess={...} /></Document>
  // 3. on render success, capture rendered dimensions
  // 4. overlay a div positioned with (x1, y1, x2, y2) * dimensions
}
```

**Decision: no zoom or pan controls.** A static page render with a static highlight is enough for the demo. Zoom/pan adds two new UI states to polish.

### `apps/dashboard/components/audit-trail.tsx`

Server component. Loads the `AuditEvent` rows for an Issue's `CorrelationId` (or for a `ReadinessScore.Id` — pick whichever the score-compute audit chain anchors on; align with P0 `IAuditWriter`'s correlation pattern) and renders a vertical timeline:

```
classify   Haiku 4.5      $0.0003    1.2s   [Langfuse →]
extract    Sonnet 4.6     $0.022     5.4s   [Langfuse →]
validator  identity_co…              0.8s   [Langfuse →]
score      synth          —          0.1s   —
```

Compact. Mono font. One row per audit event in the chain. Per-row Langfuse deep-link computed from `LangfuseTelemetry.TryGetApiBase` + the event's trace id.

### `apps/dashboard/components/issue-card.tsx` (extend)

P1's IssueCard opens a single Sheet with remediation + citations. P6 splits the right-panel area into two tabs:

- **Drill-in** — citation list with embedded `PdfPreview` per citation (page + bbox).
- **Why we flagged this** — the `AuditTrail` for this Issue's audit chain.

Tab state is local to the Sheet; resets on close.

### `tools/Seed/Program.cs` (extend)

Add a `--demo` flag. When set, the seed:
1. Wipes `providers` (CASCADE as today).
2. Inserts 3 providers at fixed Guids:
   - `11111111-1111-1111-1111-111111111111` — Dr. Iris Green (P2 green fixture, score 100)
   - `22222222-2222-2222-2222-222222222222` — Dr. Marcus Yellow (P2 yellow fixture, score 62)
   - `33333333-3333-3333-3333-333333333333` — Dr. Cassandra Red (P2 red fixture, score 34)
3. Computes each provider's score so the demo URL bar never shows "loading…".

The fixed Guids matter — the demo script links to specific provider URLs. A reseeded random Guid breaks the script.

### `docs/demo-script.md`

5 beats, ~60s each. Each beat names the URL, the click, the spoken line. Skeleton:

```
[0:00–0:30] Open on the score, not the intake
  url:     /providers
  click:   "Dr. Marcus Yellow"
  say:     "PacketReady scores a credentialing packet 0..100 before it goes to a payer.
            This provider scores 62. One Critical, one Major, one Minor. The score is
            the headline; the issues below tell you what to fix."

[0:30–1:30] Drill into the Critical
  click:   the License Critical issue
  switch:  to the Drill-in tab
  say:     "Every Issue cites the source. Here's the license PDF, page 1, the
            highlighted region is where we extracted the expired-date from. The
            extracted value carries through to the dashboard verbatim."

[1:30–2:15] Show the audit trail
  click:   the Why-we-flagged-this tab
  say:     "Behind every Issue: a Haiku classification, a Sonnet extraction, a
            validator invocation. Every step has cost, latency, and a Langfuse link.
            One Critical Issue costs about two cents."

[2:15–3:45] Rewind to intake
  url:     /admin
  click:   Add Provider
  show:    magic-link email in MailHog
  click:   the link → upload 4 PDFs + answer 2 questions
  say:     "Provider gets one email, replies once. The agent decides when we have
            enough to score. No 47-field form. No 12 follow-up reminders."

[3:45–5:00] Round trip
  show:    second turn fires (Langfuse trace)
  show:    score appears at /providers/{newId}
  say:     "End on the screen we opened with. The intake produced the data the
            score reads. That's the system."
```

### README.md (rewrite hero)

Three short blocks above the fold:

```
# PacketReady — pre-CAQH packet readiness scoring

[Loom embed]

PacketReady scores a credentialing packet 0..100 before it goes to the payer.
50 synthetic packets, 4 doc types, 7 validators (4 rule-based + 2 LLM-augmented + 1 payer-aware).

| Metric                        | Value |
|---|---|
| Extraction accuracy (clean)   | …%   |
| Extraction accuracy (scanned) | …%   |
| Conflict recall (planted)     | …%   |
| Conflict precision (planted)  | …%   |
| Tier agreement (κ, n=20)      | …    |

[bias-caveat paragraph from the P4 README]

→ design.md · build-plan.md · phase docs
```

Numbers come from `evals/results/baseline.json` after P4 closes. **Don't quote pre-P4 numbers**; they're the 5-packet harness sanity check, not a benchmark.

---

## Task order

1. **Reread the design-doc demo script (§13).** Block off the 5 beats; sketch the URLs and click sequence on paper before touching code.
2. **Seed `--demo` flag** with 3 fixed-Guid providers + post-insert score compute. Smoke: hit each URL, see the right tier.
3. **`PdfPreview` component** (offline first — hardcode a documentId + page + bbox; verify the overlay math against a known PDF).
4. **`/api/documents/{id}/blob` endpoint** if it doesn't exist. (If P3 didn't ship a blob-serving endpoint, this is one new route + a `BlobStore.Open` call.)
5. **Wire `PdfPreview` into the citation list** inside the IssueCard's drill-in tab.
6. **`AuditTrail` component** — Server component that queries audit_events by correlation id.
7. **Split IssueCard's Sheet into two tabs** (Drill-in + Why we flagged this). Don't redesign — Radix Tabs inside the existing Sheet.
8. **`docs/demo-script.md`** committed with the spoken lines. Read it aloud once — flags awkward phrases earlier than rehearsal does.
9. **Polish loading / empty / error states** on every screen the script touches:
   - List page: 3 demo rows present always; no spinner.
   - Detail page: PDF preview's "loading" state is a placeholder rect of the same size as the rendered page (no layout shift).
   - Intake portal: success / "thanks, we'll follow up" terminal screen.
10. **Record a dry run.** Don't try to make it perfect. Watch back. List what feels wrong.
11. **Address top 3 issues.** Don't fix everything — only what makes the demo *unwatchable*.
12. **Re-record.** Watch back. If the cuts feel like cuts, list-and-fix once more.
13. **README hero** — paste in numbers from `evals/results/baseline.json`, embed Loom, link competitor table. Above the fold on a 13".
14. **Download MP4 backup** of the Loom. Commit nothing; just save it locally to a known path.
15. **Gate verification.**

Order matters: 2 unblocks 5/6/7; 4 unblocks 3; 8 should happen before 10 (you can't dry-run without a script); 11/12 may iterate.

---

## Risks / open

- **Polish creep.** The whole phase. "While I'm in there" is the failure mode. Stop when the demo *records cleanly*, not when the UI is "nice." Anything off-screen during the 5 minutes does not get touched.
- **PDF preview performance.** `pdfjs-dist` can be slow on first render. For demo, prewarm by navigating to the detail page during seed verification; the second-time-around render is fast. Not solving the cold-load latency in P6.
- **Loom-link rot.** Free Loom tiers can expire / re-encode / lose chapters. The MP4 backup (step 14) is the durable copy. Re-upload to S3 if Loom becomes hostile.
- **Demo-data drift.** If `evals/dataset/` regenerates between P4 and P6 (different ReportLab version, different Pillow version), the demo videos showing specific extracted values can mismatch reality. Lock the dataset state to a commit hash before recording; don't regenerate during P6.
- **Audit chain assumes correlation id is set.** If P0's `AuditEvent.CorrelationId` was added but no handler actually sets it on the score-compute chain, the AuditTrail component has nothing to query. Verify in step 6 before wiring the UI.
- **README numbers depend on P4.** If P4 isn't closed, the README hero is aspirational. Don't ship the hero without real numbers — write a draft and gate the merge on P4 close.
- **Recording on a small screen.** Loom captures whatever resolution the browser renders at. Use a 1440×900 viewport (looks fine on most reviewers' screens) and a clean browser profile (no extensions in the toolbar). One-time setup, easy to forget.

---

## Out of scope (resist)

- **Auth / multi-tenant / SSO.** Still synthetic-data demo. The Atano hand-off post-P6 may revisit; not P6.
- **Visual-design overhaul.** shadcn defaults stay. No custom theme, no logo work, no font swaps.
- **A11y audit.** Note as a gap in the README; don't fix in P6.
- **i18n.** English only.
- **Continuous integration.** The regression gate still runs operator-side; CI integration is a post-launch ask.
- **Real-time updates / SSE / SignalR.** The dashboard refreshes on navigation. Live updates are out.
- **Mobile / responsive polish beyond 1440×900.** The demo is recorded on a laptop. Mobile is post-launch.
- **Recording in 4K.** Loom default is fine. Don't fight the encoder.
- **A second demo for a different persona.** One demo. One script. One recording.
- **Backend changes.** If P6 touches anything under `apps/api/`, the change has scope-crept.

---

## What gets written when Phase 6 closes

PacketReady is shippable. Open `docs/closing-notes.md` (new) with:
- The committed `evals/results/baseline.json` snapshot.
- The Loom URL + MP4 path.
- A one-paragraph reflection: what the build proves, what it doesn't, what the next 2 weeks would do.
- The Atano-outreach email draft, if not already sent.

This is the end of the build phase. Anything after P6 is **maintenance + outreach + post-launch features** — a different mode of work, and the build-plan stops here on purpose.
