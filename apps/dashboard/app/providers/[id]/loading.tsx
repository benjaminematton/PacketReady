/**
 * Skeleton for the provider detail page. Matches the rendered layout (back
 * link → header with score badge → breakdown bar → issue list) so the
 * swap doesn't move the score badge laterally during the demo.
 */
export default function ProviderDetailLoading() {
  return (
    <main className="mx-auto max-w-3xl px-6 py-10">
      <div className="mb-6 h-3 w-24 animate-pulse rounded bg-zinc-200 dark:bg-zinc-800" />

      <header className="mb-8 flex items-start justify-between gap-6">
        <div className="min-w-0 flex-1 space-y-3">
          <div className="h-7 w-56 animate-pulse rounded bg-zinc-200 dark:bg-zinc-800" />
          <div className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-2">
            <div className="h-3 w-10 animate-pulse rounded bg-zinc-200/70 dark:bg-zinc-800/70" />
            <div className="h-3 w-24 animate-pulse rounded bg-zinc-200/70 dark:bg-zinc-800/70" />
            <div className="h-3 w-20 animate-pulse rounded bg-zinc-200/70 dark:bg-zinc-800/70" />
            <div className="h-3 w-12 animate-pulse rounded bg-zinc-200/70 dark:bg-zinc-800/70" />
          </div>
        </div>
        <div className="h-10 w-16 shrink-0 animate-pulse rounded-full bg-zinc-200 dark:bg-zinc-800" />
      </header>

      <div className="mb-4 flex items-center gap-3">
        {[0, 1, 2].map((i) => (
          <div
            key={i}
            className="h-7 w-20 animate-pulse rounded-md bg-zinc-200/80 dark:bg-zinc-800/80"
          />
        ))}
      </div>

      <ul className="space-y-2">
        {[0, 1, 2].map((i) => (
          <li
            key={i}
            className="flex items-start gap-4 rounded-lg border border-zinc-200 bg-card px-5 py-4 dark:border-zinc-800"
          >
            <div className="h-5 w-16 shrink-0 animate-pulse rounded-md bg-zinc-200 dark:bg-zinc-800" />
            <div className="min-w-0 flex-1 space-y-2">
              <div className="h-4 w-3/4 animate-pulse rounded bg-zinc-200 dark:bg-zinc-800" />
              <div className="h-3 w-2/3 animate-pulse rounded bg-zinc-200/60 dark:bg-zinc-800/60" />
            </div>
            <div className="h-3 w-16 shrink-0 animate-pulse rounded bg-zinc-200/60 dark:bg-zinc-800/60" />
          </li>
        ))}
      </ul>
    </main>
  );
}
