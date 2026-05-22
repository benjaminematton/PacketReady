import Link from "next/link";

export default function ProviderNotFound() {
  return (
    <main className="mx-auto max-w-3xl px-6 py-10">
      <div className="rounded-lg border border-zinc-200 bg-zinc-50 px-6 py-10 text-center dark:border-zinc-800 dark:bg-zinc-900">
        <p className="text-base font-medium text-zinc-900 dark:text-zinc-100">
          Provider not found.
        </p>
        <p className="mt-2 text-sm text-zinc-500 dark:text-zinc-400">
          The provider id in the URL doesn&apos;t match any row.
        </p>
        <Link
          href="/providers"
          className="mt-6 inline-block text-sm text-zinc-700 underline hover:text-zinc-900 dark:text-zinc-300 dark:hover:text-zinc-100"
        >
          Back to all providers
        </Link>
      </div>
    </main>
  );
}
