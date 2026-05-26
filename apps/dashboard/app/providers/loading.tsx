/**
 * Skeleton for the provider list. Shown while the server component fetches
 * `api.listProviders()`. Match the rendered shape closely so the swap from
 * skeleton → real list doesn't cause layout shift mid-demo.
 *
 * Server component; no client JS. The skeleton animation is pure CSS via
 * Tailwind's `animate-pulse`.
 */
export default function ProvidersLoading() {
  return (
    <main className="mx-auto max-w-3xl px-6 py-10">
      <header className="mb-8">
        <div className="h-7 w-32 animate-pulse rounded bg-zinc-200 dark:bg-zinc-800" />
        <div className="mt-2 h-4 w-48 animate-pulse rounded bg-zinc-200/70 dark:bg-zinc-800/70" />
      </header>

      <ul className="divide-y divide-zinc-200 rounded-lg border border-zinc-200 bg-white dark:divide-zinc-800 dark:border-zinc-800 dark:bg-zinc-950">
        {[0, 1, 2].map((i) => (
          <li
            key={i}
            className="flex items-center justify-between gap-4 px-5 py-4"
          >
            <div className="min-w-0 flex-1 space-y-2">
              <div className="h-4 w-40 animate-pulse rounded bg-zinc-200 dark:bg-zinc-800" />
              <div className="h-3 w-32 animate-pulse rounded bg-zinc-200/60 dark:bg-zinc-800/60" />
            </div>
            <div className="h-7 w-12 shrink-0 animate-pulse rounded-full bg-zinc-200 dark:bg-zinc-800" />
          </li>
        ))}
      </ul>
    </main>
  );
}
