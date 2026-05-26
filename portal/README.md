# PacketReady — Provider Intake Portal

Single-page Next.js 15 app the provider hits via the magic link emailed
by `POST /api/intakes`. Server component renders the greeting + state;
a server action POSTs to `/api/portal/{token}/submit` and redirects on
ack. No client-state library, no client components in v1.

## Run

```
cd portal
npm install
API_BASE_URL=http://localhost:5065 npm run dev
```

Open the URL the dispatched `.eml` file points at (under
`apps/api/Api/outbox/sent/{yyyy-mm-dd}/{id}.eml` once the .NET API +
`OutboxDispatcherJob` have done their work). The Next.js dev server
binds port `3002`; the email body embeds the path `/portal/{token}`,
so the full URL is `http://localhost:3002/portal/{token}`.

## Env

| Var | Default | Purpose |
|---|---|---|
| `API_BASE_URL` | `http://localhost:5065` | .NET API base. **Server-side only** — never expose as `NEXT_PUBLIC_*`. |

The .NET HTTPS dev port (`5066`) uses a self-signed cert that Node's
fetch refuses by default; the HTTP port works without ceremony.

## Layout

```
portal/
├── app/
│   ├── layout.tsx                     # html shell + globals.css import
│   ├── page.tsx                       # root landing (not the portal)
│   ├── globals.css                    # Tailwind v4 entry + tokens
│   └── portal/
│       └── [token]/
│           ├── page.tsx               # server component — fetch state + render
│           ├── actions.ts             # server action — POST submit + redirect
│           └── submitted/
│               └── page.tsx           # static ack
├── components/
│   └── extraction-card.tsx            # §7.9 doc card: fields + confidence
└── lib/
    └── api.ts                         # typed client for /api/portal/{token}
```

## What's *not* in v1

Extraction cards now render (read-only) — the page lists every
uploaded document with its latest extracted fields + per-field
confidence + classifier confidence. Per-field **edit** (the §7.9
`source='provider_edit'` append path) is still deferred.

Also deferred:

- Drag-and-drop document upload. Uploads happen via the .NET API's
  `POST /api/providers/{id}/documents` from P3; for the demo loom an
  operator uploads via `curl` before sharing the magic link.
- Per-field confirm / edit UI from `design.md §7.9`. The cards read;
  they don't write. Editing a row would append a new
  `document_extractions` row with `source='provider_edit'` and trigger
  a cascading validator re-run; both wiring + UI are TBD.
- PDF preview with bbox highlighting next to each field. The
  `fieldLocationsJson` payload carries `{ page, bbox: [x,y,w,h] }`,
  the dashboard already renders this; the portal hasn't yet.
- Re-issue admin endpoint UI. Today an admin re-runs `POST /api/intakes`
  (which 409s since `IntakeAlreadyExistsException` fires); the
  recovery path is a future .NET endpoint, not this page.

## Why server-only

The DoD pins "no client-state library" and the page does zero
interactive work besides the submit button — a `<form action={…}>`
posting to a server action is the smallest correct primitive. Keeping
everything server-side also lets `lib/api.ts` read `API_BASE_URL` from
the environment without leaking it into the browser bundle.

## Tailwind v4 note

Tailwind v4 ships its PostCSS plugin separately
(`@tailwindcss/postcss`). The v3 entry point is gone. `postcss.config.mjs`
references the new plugin; `app/globals.css` uses `@import "tailwindcss"`
rather than the old `@tailwind base / components / utilities` triad.
