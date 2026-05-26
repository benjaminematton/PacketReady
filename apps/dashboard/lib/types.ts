/**
 * Wire-format types mirroring the .NET DTOs. Hand-maintained — keep in sync with:
 *   - apps/api/Application/Providers/Queries/ListProviders/ListProvidersQuery.cs
 *   - apps/api/Application/Providers/Queries/GetProviderDetail/GetProviderDetailQuery.cs
 *   - apps/api/Application/Scoring/Commands/ComputeReadinessScore/ComputeReadinessScoreCommand.cs
 *   - apps/api/Domain/Scoring/{Severity,Tier,Issue,Citation}.cs
 *
 * Convention notes:
 *   - `DateTimeOffset` on the wire is an ISO 8601 string (e.g. "2026-05-21T20:45:10.84+00:00").
 *     Typed as `string` here; parse to `Date` at display time only.
 *   - Enums serialize as strings via JsonStringEnumConverter in the API.
 *   - camelCase property names enforced by PropertyNamingPolicy.CamelCase in DomainJson.
 *   - Neither `Issue` nor `Citation` carries a stable id. React lists must
 *     synthesize keys from the row's own fields (validator + index is fine).
 */

export type Severity = "Critical" | "Major" | "Minor";
export type Tier = "Red" | "Yellow" | "Green";

/** Axis-aligned bbox in normalized PDF page coordinates (top-left origin, 0..1). */
export interface BoundingBox {
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

/**
 * Cross-reference from an Issue back to where it came from. Phase 1 carries
 * validator name + extracted value; the optional doc-ref fields populate in P3.
 * `lowConfidence` (P4) flags individual citations whose underlying extracted
 * field had < 0.85 extractor confidence; the ConfidenceGuard downgrades the
 * parent Issue if any of its citations carries the flag.
 */
export interface Citation {
  sourceValidator: string;
  extractedValue: string;
  documentId: string | null;
  page: number | null;
  bbox: BoundingBox | null;
  lowConfidence?: boolean;
}

/**
 * P4 ConfidenceGuard-stamped fields:
 *   - `isLowConfidenceInput`: this Issue was Critical pre-guard and got
 *     downgraded to Minor because at least one cited field landed below
 *     the 0.85 confidence threshold. The dashboard renders a pill so the
 *     operator can tell "tier moved because of confidence" from "tier
 *     moved because of credential state."
 *   - `field`: discriminator naming the specific field an LLM-validator
 *     finding is about (e.g. "malpractice.fullName"); empty for pure-code
 *     validators.
 *   - `code`: stable code identifying the kind of aggregator-emitted Issue
 *     (see Domain/Scoring/IssueCodes.cs).
 *   - `missingDocType`: doc-type tag set on aggregator missing/failed/partial
 *     Issues so the dashboard can group by underlying document.
 */
export interface Issue {
  validator: string;
  severity: Severity;
  message: string;
  remediation: string;
  citations: Citation[];
  isLowConfidenceInput?: boolean;
  field?: string;
  code?: string;
  missingDocType?: string | null;
}

export interface ReadinessScore {
  id: string;
  providerId: string;
  score: number;
  tier: Tier;
  criticalCount: number;
  majorCount: number;
  minorCount: number;
  issues: Issue[];
  computedAt: string;
}

/** Row shape for GET /api/providers. Score fields null when no score yet. */
export interface ProviderListItem {
  id: string;
  fullName: string;
  latestScore: number | null;
  latestTier: Tier | null;
  latestComputedAt: string | null;
}

/** GET /api/providers/{id} response. `latestScore` is null only pre-compute. */
export interface ProviderDetail {
  id: string;
  fullName: string;
  npi: string;
  credentialingState: string;
  createdAt: string;
  latestScore: ReadinessScore | null;
}

/**
 * RFC 7807 ProblemDetails. The API emits machine-readable URN `type` values
 * (e.g. "urn:packetready:error:provider_not_found"); branch on those, not
 * on `title` or status code. The index signature carries per-error extensions
 * (e.g. `providerId` for not-found) as `unknown` — narrow at the read site.
 */
export interface ProblemDetails {
  type: string;
  title: string;
  status: number;
  detail?: string;
  instance?: string;
  [extension: string]: unknown;
}

export const ProblemTypes = {
  ProviderNotFound: "urn:packetready:error:provider_not_found",
  EmptyProviderId: "urn:packetready:error:empty_provider_id",
} as const;

export type ProblemType = (typeof ProblemTypes)[keyof typeof ProblemTypes];
