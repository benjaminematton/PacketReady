// Static ack page rendered after the server action redirects. No
// state fetched — the dynamic data (when the agent fires, what gets
// composed) lives in the .NET audit log, not on a provider-facing
// page. Showing more would be misleading: the agent turn hasn't run
// yet at redirect time.

export default function SubmittedPage() {
  return (
    <main className="mx-auto max-w-2xl px-6 py-16">
      <h1 className="text-3xl font-semibold tracking-tight">
        Thanks — we're on it.
      </h1>
      <p className="mt-4 text-[color:var(--foreground)]">
        We received your submission. PacketReady will reach out by email
        if we need anything else; otherwise you're done.
      </p>
      <p className="mt-6 text-sm text-[color:var(--muted)]">
        You can close this tab.
      </p>
    </main>
  );
}
