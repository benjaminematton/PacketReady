"use client";

// Tiny client island for the submit button. Server actions don't expose
// their pending state to the surrounding server component, so this is
// the standard Next.js pattern: a `<form action={...}>` lives in a
// server component, and `useFormStatus` reads the pending bit from a
// child client component.
//
// Single-use submits are safe at the API level (the second click hits a
// Consumed magic link and the page redirects), but disabling the button
// during the action removes the dead-air window where the page looks
// frozen — particularly visible on a slow demo network.

import { useFormStatus } from "react-dom";

export function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      aria-disabled={pending}
      className="inline-flex items-center justify-center rounded-md bg-neutral-900 px-6 py-3 text-sm font-medium text-white transition hover:bg-neutral-700 focus:outline-none focus:ring-2 focus:ring-neutral-900 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-60 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-neutral-300 dark:focus:ring-neutral-100"
    >
      {pending ? "Submitting…" : "Submit my intake"}
    </button>
  );
}
