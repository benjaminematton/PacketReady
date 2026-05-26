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
└── lib/
    └── api.ts                         # typed client for /api/portal/{token}
```

## What's *not* in v1

The DoD calls for "the extracted-field cards from §7.9 of the design
doc — provider sees what was pulled and confirms / edits inline before
submit." The portal page today renders the greeting + session state +
submit button only. The extraction-card UX needs the .NET
`GET /api/portal/{token}` endpoint to return documents +
extractions; that's a follow-up.

Also deferred:

- Drag-and-drop document upload. Uploads happen via the .NET API's
  `POST /api/providers/{id}/documents` from P3; for the demo loom an
  operator uploads via `curl` before sharing the magic link.
- Per-field confirm/edit UI from `design.md §7.9`. Same blocker as
  above — needs the GET endpoint extended.
- Re-issue admin endpoint UI. Today an admin re-runs `POST /api/intakes`
  (which 409s since `IntakeAlreadyExistsException` fires); the
  recovery path is a future endpoint, not this page.

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
