// Root landing — not the intake portal. Providers reach the portal
// via /portal/{token}; anyone hitting / directly gets a plain pointer
// instead of a confusing blank screen.

export default function Home() {
  return (
    <main className="mx-auto max-w-2xl px-6 py-16">
      <h1 className="text-2xl font-semibold tracking-tight">PacketReady</h1>
      <p className="mt-4 text-[color:var(--muted)]">
        This page isn't a sign-in. Open the magic-link URL from your
        intake email — it lands directly on your portal.
      </p>
    </main>
  );
}
