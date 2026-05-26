import { fetchPortalState, type MagicLinkInvalidReason } from "@/lib/api";
import { ExtractionCard } from "@/components/extraction-card";
import { submitAction } from "./actions";
import { SubmitButton } from "./SubmitButton";

type Props = {
  params: Promise<{ token: string }>;
};

export const dynamic = "force-dynamic"; // every visit re-fetches state

// Pinned, server-deterministic expiry rendering. Without an explicit
// locale + timeZone, `toLocaleString` runs in Node's default locale and
// the container's timezone — typically UTC in prod — and a provider in
// PT sees a wall time that doesn't match their inbox. PacketReady is
// single-region (US/Pacific) for now; revisit when there are providers
// outside it.
const EXPIRY_FORMATTER = new Intl.DateTimeFormat("en-US", {
  // Spec forbids combining `timeZoneName` with `dateStyle`/`timeStyle`,
  // so expand the components manually. Keeps the rendered string
  // unambiguous about which clock it's quoted in.
  year: "numeric",
  month: "long",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
  timeZone: "America/Los_Angeles",
  timeZoneName: "short",
});

export default async function PortalPage({ params }: Props) {
  const { token } = await params;
  const state = await fetchPortalState(token);

  if ("kind" in state) {
    return (
      <main className="mx-auto max-w-3xl px-6 py-16">
        <h1 className="text-2xl font-semibold tracking-tight">
          Link no longer valid
        </h1>
        <p className="mt-4 text-[color:var(--foreground)]">
          {state.kind === "magic_link_invalid"
            ? formatReason(state.reason)
            : `Couldn't reach the intake server (${state.status}).`}
        </p>
        <p className="mt-4 text-sm text-[color:var(--muted)]">
          Ask the admin to issue a fresh link.
        </p>
      </main>
    );
  }

  const greeting = state.providerFullName ?? "there";
  const expiresAt = EXPIRY_FORMATTER.format(new Date(state.linkExpiresAt));

  // Submit is gated strictly on AwaitingProvider. `Pending` means the
  // outbox dispatcher hasn't fired `NotifyInvitationSent` yet —
  // submitting there races the dispatcher: `BeginAgentTurn` requires
  // `AwaitingProvider` (IntakeSession.cs:114), so the IntakeTurnJob
  // enqueued by the submit will throw if it runs before the transition
  // lands. AgentProcessing / Complete / Escalated are terminal-ish for
  // this surface — show a per-state message instead.
  const canSubmit = state.sessionState === "AwaitingProvider";

  return (
    <main className="mx-auto max-w-3xl px-6 py-16">
      <h1 className="text-3xl font-semibold tracking-tight">
        Hi, {greeting}.
      </h1>
      <p className="mt-4 text-[color:var(--foreground)]">
        Welcome to PacketReady's credentialing intake. We've started a
        session for you. Submit below to let us know you've reviewed
        everything you've shared; we'll take it from there.
      </p>

      <section className="mt-10 rounded-lg border border-neutral-200 bg-neutral-50 p-6 dark:border-neutral-800 dark:bg-neutral-900">
        <h2 className="text-lg font-medium">Intake status</h2>
        <dl className="mt-4 grid grid-cols-2 gap-x-6 gap-y-3 text-sm">
          <dt className="text-[color:var(--muted)]">Session state</dt>
          <dd className="font-mono">{state.sessionState}</dd>
          <dt className="text-[color:var(--muted)]">Link expires</dt>
          <dd>{expiresAt}</dd>
        </dl>
      </section>

      {state.documents.length > 0 ? (
        <section className="mt-10 space-y-4">
          <h2 className="text-lg font-medium">What we have on file</h2>
          <p className="text-sm text-[color:var(--muted)]">
            These are the documents you've sent us and what we read from
            each. Glance through before submitting — if anything looks
            wrong, reply to the email and we'll fix it.
          </p>
          {state.documents.map((doc) => (
            <ExtractionCard key={doc.documentId} document={doc} />
          ))}
        </section>
      ) : (
        <section className="mt-10 rounded-lg border border-dashed border-neutral-300 p-6 dark:border-neutral-700">
          <p className="text-sm text-[color:var(--muted)]">
            We don't have any documents on file for you yet. Reply to the
            intake email with attachments (license, DEA, malpractice,
            board cert) and they'll show up here.
          </p>
        </section>
      )}

      {canSubmit ? (
        <form
          action={submitAction.bind(null, token)}
          className="mt-10 space-y-3"
        >
          <SubmitButton />
          <p className="text-sm text-[color:var(--muted)]">
            Single-use — submitting consumes this link. If you need to
            come back later, ask the admin to re-issue one.
          </p>
        </form>
      ) : (
        <section className="mt-10 rounded-lg border border-neutral-200 p-6 dark:border-neutral-800">
          <p className="text-sm text-[color:var(--foreground)]">
            {messageForState(state.sessionState)}
          </p>
        </section>
      )}
    </main>
  );
}

// Provider-facing copy for each reason. Pinned strings rather than a
// generic ".toLowerCase()" so future enum additions surface as
// "Link invalid: <new>" loud-enough to notice during a demo. The `string`
// arm matches an unknown reason from a future API rev — `MagicLinkInvalidReason`
// is the closed enum of today's known values.
function formatReason(reason: MagicLinkInvalidReason | (string & {})): string {
  switch (reason) {
    case "Expired":
      return "This link has expired.";
    case "Consumed":
      return "This link has already been used.";
    case "NotFound":
      return "This link isn't on file. Check the URL or ask for a new one.";
    case "BadSignature":
    case "Malformed":
      return "This link looks tampered with. Ask the admin for a fresh one.";
    default:
      return `Link invalid: ${reason}.`;
  }
}

function messageForState(state: string): string {
  switch (state) {
    case "Pending":
      // Narrow race window between StartIntake and the outbox dispatcher
      // moving us to AwaitingProvider. Reload usually clears it.
      return "We're still setting up your session. Reload this page in a few seconds.";
    case "AgentProcessing":
      return "Your previous submission is being reviewed. Check back in a few minutes — you'll get an email when there's something for you.";
    case "Complete":
      return "Your intake is complete. Nothing left to do here.";
    case "Escalated":
      return "Your intake has been routed to a human reviewer. They'll be in touch by email.";
    default:
      return `Session is ${state}; submitting isn't available right now.`;
  }
}
