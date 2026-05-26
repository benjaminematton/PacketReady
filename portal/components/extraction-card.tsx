import type { PortalDocument } from "@/lib/api";

/**
 * One document's extraction view — matches the §7.9 "extraction card"
 * shape: doc type + filename + per-field rows. v1 is read-only.
 * Per-field confirm / edit (the source='provider_edit' append path)
 * is a follow-up.
 *
 * JSONB blobs come through as strings; we parse each lazily and
 * fall back to "—" on parse failure. The page never blows up over
 * malformed extractor output — that'd be the worst possible failure
 * mode from the provider's POV.
 */
export function ExtractionCard({ document: doc }: { document: PortalDocument }) {
  const fields = safeParseObject(doc.latestExtraction?.fieldsJson);
  const confidences = safeParseObject<number>(
    doc.latestExtraction?.confidenceJson,
  );

  // Sorted field rows for deterministic rendering across reloads. The
  // extractor emits fields in schema-declaration order; without this
  // sort the order is "whatever STJ serialized," which can flip.
  const fieldNames = Object.keys(fields).sort();

  return (
    <article className="rounded-lg border border-neutral-200 bg-neutral-50 p-6 dark:border-neutral-800 dark:bg-neutral-900">
      <header className="flex items-baseline justify-between">
        <div>
          <h3 className="text-lg font-medium">{prettyDocType(doc.docType)}</h3>
          <p className="mt-1 text-sm text-[color:var(--muted)]">
            {doc.originalName}
            <span className="mx-2">·</span>
            {doc.pageCount} page{doc.pageCount === 1 ? "" : "s"}
            {doc.docTypeConfidence != null ? (
              <>
                <span className="mx-2">·</span>
                classifier {Math.round(doc.docTypeConfidence * 100)}%
              </>
            ) : null}
          </p>
        </div>
        <ExtractionBadge extraction={doc.latestExtraction} />
      </header>

      {doc.latestExtraction == null ? (
        <p className="mt-6 text-sm text-[color:var(--muted)]">
          We've stored this file but haven't extracted its fields yet.
          You'll see the parsed values here once that runs.
        </p>
      ) : fieldNames.length === 0 ? (
        <p className="mt-6 text-sm text-[color:var(--muted)]">
          The extractor returned no fields for this document.
        </p>
      ) : (
        <dl className="mt-6 grid grid-cols-[minmax(8rem,auto)_1fr_auto] gap-x-6 gap-y-3 text-sm">
          {fieldNames.map((name) => (
            <FieldRow
              key={name}
              name={name}
              value={fields[name]}
              confidence={confidences[name]}
            />
          ))}
        </dl>
      )}
    </article>
  );
}

function FieldRow({
  name,
  value,
  confidence,
}: {
  name: string;
  value: unknown;
  confidence: number | undefined;
}) {
  return (
    <>
      <dt className="font-mono text-[color:var(--muted)]">
        {name}
      </dt>
      <dd className="break-words">{renderValue(value)}</dd>
      <dd className="text-right tabular-nums text-[color:var(--muted)]">
        {confidence != null ? `${Math.round(confidence * 100)}%` : "—"}
      </dd>
    </>
  );
}

function ExtractionBadge({
  extraction,
}: {
  extraction: PortalDocument["latestExtraction"];
}) {
  if (extraction == null) {
    return (
      <span className="rounded-full bg-amber-100 px-3 py-1 text-xs font-medium text-amber-900 dark:bg-amber-900/30 dark:text-amber-200">
        not extracted yet
      </span>
    );
  }
  return (
    <span className="rounded-full bg-emerald-100 px-3 py-1 text-xs font-medium text-emerald-900 dark:bg-emerald-900/30 dark:text-emerald-200">
      {extraction.schemaVersion}
    </span>
  );
}

function renderValue(value: unknown): string {
  if (value == null || value === "") return "—";
  if (Array.isArray(value)) return value.map((v) => String(v)).join(", ");
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}

function prettyDocType(docType: string): string {
  // PascalCase → space-separated. "BoardCert" → "Board Cert", "Dea" → "DEA".
  if (docType === "Dea") return "DEA";
  if (docType === "Cv") return "CV";
  return docType.replace(/([a-z])([A-Z])/g, "$1 $2");
}

function safeParseObject<T = unknown>(
  json: string | undefined,
): Record<string, T> {
  if (!json) return {};
  try {
    const parsed = JSON.parse(json);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed)
      ? (parsed as Record<string, T>)
      : {};
  } catch {
    return {};
  }
}
