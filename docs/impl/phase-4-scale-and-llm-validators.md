# Phase 4 — Scale to 50 + LLM Validators + Published Numbers

> The phase where the accuracy claim becomes citable. P2's harness + P3's extractors now run against 50 NPPES-sampled packets; two new LLM-augmented validators add cross-document reasoning; the README ships with live numbers.

| | |
|---|---|
| **Parent** | [build-plan.md](../build-plan.md) — Phase 4 row |
| **Goal** | The README quotes real accuracy + conflict precision/recall + score correlation. The regression gate guards them. |
| **Status** | Not started |
| **Data** | 50 synthetic packets · NPPES-sampled distributions · hand-labeled tiers on 20 of them · no PHI |
| **Depends on** | [Phase 2](./phase-2-eval-harness.md) — closed 2026-05-22 · [Phase 3](./phase-3-extractors.md) — extractors + per-field confidence emitted |
| **Style** | [../style.md](../style.md) |

---

## Definition of done

- [ ] `evals/dataset/` holds **50 packets** in 4 buckets: 15 clean+valid · 15 clean+conflicts · 15 scanned+clean · 5 scanned+conflicts. Generated programmatically from NPPES distributions with a locked random seed; **deterministic field values; PDF bytes may vary across ReportLab versions and platform JPEG encoders**.
- [ ] **Per-field confidence** lands on every extraction row. **Already shipped in P3 slice 8** as a `confidence` JSONB column on `document_extractions` (singular; see [DocumentExtractionConfiguration.cs](../../apps/api/Infrastructure/Persistence/Configurations/DocumentExtractionConfiguration.cs)) and `FieldProvenance.Confidence` through the aggregator (see [FieldProvenance.cs](../../apps/api/Application/Providers/Aggregation/FieldProvenance.cs)). Float in `[0, 1]`. P4 only wires the *consumer*: `ProvenanceExtensions.Cite` stamps `Citation.LowConfidence`, and `ConfidenceGuard` folds it into the issue list.
- [ ] **Two new LLM-augmented validators wired:** `identity_coherence` (Sonnet structured output across all extractions for one provider; covers `fullName`/`dateOfBirth`/`npi`/`address` cross-doc agreement) and `npi_taxonomy_match` (CSV-deterministic lookup of taxonomy code → canonical specialty, then a thin Sonnet "does stated specialty match canonical?" call). Both registered in `AddApplication()` and exercised by `ComputeReadinessScoreCommand`.
- [ ] **Three new payer-aware validators wired:** `malpractice_currency` (status + coverage ≥ payer minimum + ≥ 30-day window; owns the full malpractice surface — no other malpractice validator exists), `required_documents` (per-payer required-doc-type presence), and `BoardCertificationValidator` extended to consume payer config for the accepted-boards list. Two sample payers committed.
- [ ] **Conflict precision + recall** reported per `plantedConflict` kind. Definition: a planted conflict is "caught" iff **all three** of: (1) at least one Issue's `Validator` matches the expected validator for that kind, (2) the Issue's citations name at least one of the planted `sources`, and (3) the Issue's `Field` (on LLM-emitted Issues) matches the planted conflict's target field. Precision = caught-and-planted / all-flagged-conflicts; recall = caught-and-planted / total-planted.
- [ ] **Tier agreement on 20 hand-labeled packets** — headline metric is **weighted Cohen's κ (quadratic weights) ≥ 0.50** (substantial-agreement floor per Landis-Koch) with the **3×3 confusion matrix** and **raw agreement rate** reported alongside. Spearman is *not* the headline (3-tier categorical labels make it unstable at n=20); kept as a footnote for design-doc continuity. Labels live at `evals/labels/human_tiers.json`, written **before** the eval runs so the labeler isn't anchored on system output (enforced by an mtime check).
- [ ] **Confidence-threshold gate** active: a Critical Issue whose citations include any field with confidence < 0.85 is downgraded to Minor and carries a structural `isLowConfidenceInput: true` flag on the Issue itself (not a message suffix). Documented in [design.md §11.1](../design.md).
- [ ] **`evals/results/baseline.json`** committed with `stub: false` and real numbers. P3 already flipped `stub`; this is the first set the regression gate guards in earnest.
- [ ] **README.md** ships with the accuracy table, conflict precision/recall, Spearman correlation, the [design.md Appendix A](../design.md) competitor comparison row, and one paragraph naming the labeler-bias caveat in plain language.
- [ ] `dotnet test` and the Python runner both pass. Two new validator test files added (one per LLM validator + one per pure-code) with at least one happy-path and one false-positive guard each.

All ten boxes check → Phase 4 closes. Move to [Phase 5 — Intake agent + outbox](./phase-5-intake-agent.md).

---

## Stack additions

| Layer | Addition | Why |
|---|---|---|
| Data | NUCC taxonomy snapshot (CSV, official) | `npi_taxonomy_match` reference. Snapshot once; don't track upstream. |
| Data | NPPES distribution sample (CSV, official) | Source of plausible state / specialty / issuance-year distributions for packet generation. |
| Python | `faker` (or similar) | Provider names + addresses. Locked to a seed so generation is deterministic. |
| Python | `scipy.stats.spearmanr` | The 20-row correlation. Six lines of pandas; not worth rolling our own. |
| Backend | YamlDotNet | Loading per-payer requirement YAMLs. .NET-native YAML parser. |

No new C# validator-framework code — `IValidator` from P1 is the shape for both LLM and pure-code validators.

---

## Decisions baked in (lock before execution)

| Decision | Choice | Why locked here |
|---|---|---|
| LLM validator output schema | Sonnet structured output (JSON schema), same response pattern as extractors | Reuses the P3 structured-output machinery; no new prompt-eval glue. |
| **Conflict kinds in P4** | `name_variant`, `taxonomy_specialty_mismatch` (2 total) | Each kind has a validator that catches it. `expiry_mismatch` from P2 is dropped — no LLM validator in P4 looks across docs for date disagreements (`IdentityCoherenceValidator.Field` is identity-only). Reintroduce with an `ExpiryConsistencyValidator` in a Phase 4.5 follow-on. |
| **Score-vs-tier agreement metric** | Weighted Cohen's κ (quadratic weights) + 3×3 confusion matrix + raw agreement. Spearman demoted to footnote. | 3-tier categorical labels + n=20 makes Spearman ρ heavy-tied and uninterpretable. Quadratic-weighted κ is the standard ordinal-categorical-agreement metric; the confusion matrix is what a reader actually wants to see. |
| **Hand-label process** | Tiers (Red/Yellow/Green) only. Solo labeler (Ben). | Numeric labels invite spurious precision at n=20. Solo because waiting for a second labeler blocks the phase. **The bias is structural**, not just acknowledged: same person who designed the cross-document reasoning rules is rating readiness. The README caveat names this in plain language — the agreement number measures self-consistency more than ground truth. A real second labeler is a post-launch ask, not a P4 gate. |
| **PayerId sourcing** | Column on `Provider`, defaults to `payer-a-national-hmo` at creation. Seed CLI varies per fixture (roughly half each payer). Admin-level payer selection at intake lands in P5. | Avoids dragging a portal-context concept into the extraction layer. Two payers in the seed exercises both YAML branches without P5 work. |
| Per-field confidence column | `confidence` JSONB on `document_extractions` (**singular**; not `confidences`), keyed by field name. **Shipped P3 slice 8.** The aggregator hydrates it into `FieldProvenance.Confidence` — that's the wire shape validators and `ConfidenceGuard` consume. | Earlier doc draft named the column `confidences`; actual column is `confidence`. ConfidenceGuard reads provenance / citation flags, not the raw JSONB. |
| Per-payer YAML location | `apps/api/Infrastructure/Payers/payers/*.yaml`, loaded at startup | One restart picks up changes. P5+ may need hot-reload; defer. |
| Confidence threshold | 0.85 for Critical-eligible inputs | Inherited from [design.md §11.1](../design.md); revisit only with data showing it's wrong. |
| **`isLowConfidenceInput` shape** | Structural `bool` field on `Issue` (and mirrored on `Citation.lowConfidence`). Not a `Message` suffix. | Suffixes double-append on re-eval, fight i18n, break downstream parsing. A structured flag is dashboard- and test-readable without string-sniffing. |

---

## Project layout deltas

```
PacketReady/
├── apps/
│   └── api/
│       ├── Application/
│       │   ├── Scoring/Validators/
│       │   │   ├── IdentityCoherenceValidator.cs        NEW (LLM)
│       │   │   ├── NpiTaxonomyMatchValidator.cs         NEW (LLM)
│       │   │   ├── MalpracticeCurrencyValidator.cs      NEW (pure; owns the full malpractice surface)
│       │   │   ├── RequiredDocumentsValidator.cs        NEW (pure, YAML-driven; per-payer required-doc presence only)
│       │   │   └── BoardCertificationValidator.cs       EXTEND (existing P1; consume payer config for accepted-boards)
│       │   ├── Prompts/
│       │   │   ├── IdentityCoherencePrompt.md           NEW
│       │   │   └── NpiTaxonomyMatchPrompt.md            NEW
│       │   └── Scoring/ConfidenceGuard.cs               NEW — Critical → Minor when low-confidence input
│       └── Infrastructure/
│           └── Payers/
│               ├── PayerRequirementLoader.cs            NEW — YAML→domain at startup
│               ├── PayerRequirement.cs                  NEW — record
│               └── payers/
│                   ├── payer-a-national-hmo.yaml        NEW
│                   └── payer-b-state-medicaid.yaml      NEW
├── data/
│   ├── nucc-taxonomy-25.1.csv                           NEW — official NUCC
│   └── nppes-sample-2026.csv                            NEW — synthetic 10k
└── evals/
    ├── dataset/
    │   ├── packet-001 … packet-005                       (P2; rebucketed under P4 IDs)
    │   └── packet-006 … packet-055                       NEW — programmatic
    ├── generators/packetready_eval/
    │   ├── nppes_sampling.py                             NEW
    │   ├── conflict_planters.py                          NEW — two planters: name_variant, taxonomy_specialty_mismatch
    │   └── packets.py                                    EXPANDS — sample, plant, render 50
    ├── runners/
    │   ├── conflict_metrics.py                           NEW — recall/precision per kind
    │   ├── correlation.py                                NEW — Spearman against hand labels
    │   └── run.py                                        EXPANDS — new metrics into baseline.json
    └── labels/
        └── human_tiers.json                              NEW — 20 packets hand-labeled
```

---

## File-by-file

### `data/nucc-taxonomy-25.1.csv`

The official NUCC code set, downloaded once from `https://www.nucc.org/images/stories/CSV/nucc_taxonomy_251.csv` and committed. 883 rows; treat as static data. NUCC publishes biannually (1/1 and 7/1) with version label `YY.0` (Jan) or `YY.1` (Jul). When NUCC 26.0 lands, commit it as `data/nucc-taxonomy-26.0.csv` alongside this file and bump the `npi_taxonomy_match` loader path — `data/README.md` carries the source URL and download date so the upgrade is mechanical.

### `data/nppes-sample-2026.csv`

A 10k-row synthetic sample of individual (Type-1) providers. Columns: NPI, primary specialty, taxonomy code, license issuance state, license issuance year. Generated by `data/build_nppes_sample.py` with a locked seed; state/specialty/year distributions derived from public aggregates (US Census 2020, AAMC 2022 Physician Specialty Data Report), Luhn-valid synthetic NPIs. The packet generator reads this file and picks rows uniformly with a locked seed. See `data/README.md` for the why-synthetic rationale.

### `evals/generators/packetready_eval/nppes_sampling.py`

```python
@dataclass(frozen=True)
class SampledProfile:
    npi: str
    primary_specialty: str
    taxonomy_code: str
    license_state: str
    license_issuance_year: int

def sample_n(seed: int, n: int, *, nppes_csv: Path) -> list[SampledProfile]:
    """
    Deterministic sample. (seed, n, file-hash) is the cache key — the
    same trio produces the same list of SampledProfiles forever.
    Provider names + addresses come from `faker` with the same seed
    so the full PACKET_SPECS list is reproducible byte-for-byte.
    """
```

### `evals/generators/packetready_eval/conflict_planters.py`

Each conflict kind is a function that mutates a `PacketSpec` in-place to introduce the disagreement and append the `plantedConflicts` marker. Keeps the disagreement self-contained — drift between PDF rendering and the conflict marker is impossible because both come from the same Python literal post-mutation.

```python
def plant_name_variant(spec: PacketSpec, rng: Random) -> None:
    """license: 'Jane Calloway'; malpractice: 'Jane C. Calloway-Smith'.
    Target field: fullName. Expected validator: identity_coherence."""

def plant_taxonomy_specialty_mismatch(spec: PacketSpec, rng: Random) -> None:
    """NPI taxonomy code on license = Cardiology; board cert specialty = Family Medicine.
    Target field: specialty. Expected validator: npi_taxonomy_match."""
```

Each planter also writes the `plantedConflicts` entry — including `kind`, `field` (the target field on the Issue side), `sources`, and `expectedSeverity`. The runner reads `field` to enforce the third predicate in the recall definition.

(`plant_expiry_mismatch` is intentionally absent. See "Conflict kinds in P4" decision + the OOS entry for `ExpiryConsistencyValidator`. Reintroduce in Phase 4.5 alongside the validator that catches it.)

### `evals/generators/packetready_eval/packets.py` (expanded)

```python
def generate_all(output_root: Path, *, seed: int = 42) -> None:
    """Idempotent: wipes and regenerates all 50 packets."""
    rng = Random(seed)
    profiles = sample_n(seed=seed, n=50, nppes_csv=NPPES_CSV)
    specs = build_specs(profiles, rng)              # buckets the 50, plants conflicts
    for spec in specs:
        write_packet(output_root / spec.id, spec)
```

Bucket assignment: deterministic from the seeded RNG, not from input order. So a future change that adds a packet to the front of the list doesn't shift every downstream packet's bucket.

### `apps/api/Application/Scoring/Validators/IdentityCoherenceValidator.cs`

```csharp
/// <summary>
/// Cross-document identity check. Reads every extracted document for one
/// provider and asks Sonnet: do the name / DOB / NPI / address fields
/// agree? Each disagreement is one Issue with citations to the disagreeing
/// sources.
///
/// <para><b>FP discipline:</b> the 30 conflict-free packets in the eval
/// set are the ground truth for "did the validator fabricate a conflict?"
/// FP rate target &lt; 5%. If the validator can't hit that without
/// dropping recall below 80%, the prompt is wrong, not the threshold.</para>
/// </summary>
public sealed class IdentityCoherenceValidator : IValidator
{
    public string Name => "identity_coherence";

    private readonly IChatClient _chat;
    private readonly IPromptLoader _prompts;
    private readonly IDocumentExtractionRepository _extractions;

    public async Task<IReadOnlyList<Issue>> RunAsync(ProviderProfile profile, CancellationToken ct)
    {
        // Pull extractions by provider id — we need per-document fields, not
        // the aggregated ProviderProfile, since the whole point is detecting
        // disagreement between source documents.
        var docs = await _extractions.GetByProviderAsync(profile.ProviderId, ct);
        if (docs.Count < 2) return Array.Empty<Issue>();    // can't conflict with self

        var systemPrompt = await _prompts.LoadAsync("IdentityCoherencePrompt.md", ct);
        var response = await _chat.GetResponseAsync<IdentityCoherenceResponse>(
            systemPrompt,
            userMessage: SerializeDocs(docs),
            ct);

        return response.Disagreements
            .Select(d => new Issue(
                Validator: Name,
                Severity: d.Severity,
                Message: d.Message,
                Remediation: d.Remediation,
                Citations: d.Sources.Select(s => new Citation(Name, s.ExtractedValue, /*…*/)).ToList()))
            .ToList();
    }
}

public sealed record IdentityCoherenceResponse(IReadOnlyList<IdentityDisagreement> Disagreements);

public sealed record IdentityDisagreement(
    string Field,                   // "fullName" | "dateOfBirth" | "npi" | "address"
    Severity Severity,              // Critical for hard mismatches; Minor for normalization differences
    string Message,
    string Remediation,
    IReadOnlyList<DisagreementSource> Sources);

public sealed record DisagreementSource(string DocType, string ExtractedValue);
```

The system prompt instructs Sonnet to **only** flag a disagreement when at least two documents show different values for the same field, and to be conservative — typo normalizations don't count. Specifics in `IdentityCoherencePrompt.md`.

### `apps/api/Application/Scoring/Validators/NpiTaxonomyMatchValidator.cs`

Two-step, not one-LLM-call:

1. **Deterministic CSV lookup.** Load the NUCC snapshot at startup into a `Dictionary<string, string>` (taxonomy code → canonical specialty). The lookup itself is `O(1)`, no LLM needed.
2. **Thin LLM compare.** Send only `{ canonicalSpecialty, statedSpecialty }` (≈50 input tokens) to Sonnet and ask "does the stated specialty semantically match the canonical one?" Structured output: `{ matches: bool, suggestedFix: string | null }`. Synonyms like "OB/GYN" vs "Obstetrics and Gynecology" or "Cardiology" vs "Cardiovascular Disease" are why this needs an LLM and not Levenshtein — but we don't need the LLM to *know* taxonomy, only to judge synonymy.

Sending the full 900-row NUCC table to Sonnet on every call would burn ~30k input tokens per validator run for no benefit. Don't.

### `apps/api/Application/Scoring/Validators/MalpracticeCurrencyValidator.cs`

Pure code. **Owns the full malpractice surface** — Phase 1 mentioned `malpractice_currency` in [design.md §7.6](../design.md) but didn't ship it, and Phase 3 didn't add it. No existing malpractice validator to merge with.

Three checks per malpractice extraction:
- Status == `Active` (Critical if not).
- Coverage limits ≥ the payer's required minimum (Major if below). Payer comes from `Provider.PayerId` (see "PayerId sourcing" decision above; defaults to `payer-a` if missing).
- Expiry: Critical if past today; Minor if within 30 days; pass otherwise.

### `apps/api/Application/Scoring/Validators/RequiredDocumentsValidator.cs`

Pure code. Single responsibility: emit Critical for any doc type the payer requires that the provider doesn't have **and** that the aggregator's universal-4 Missing-Document floor doesn't already cover.

**Missing-doc ownership split (locked — do not change without revisiting the aggregator):**

- **`IProviderProfileAggregator` owns Missing-Document Critical for the universal-4 doc types**: `License`, `DEA`, `BoardCert`, `Malpractice`. Shipped P3 — see [AggregatedProfile.cs](../../apps/api/Application/Providers/Aggregation/AggregatedProfile.cs) docstring (the `Missing-Document Critical` bullet). The existing P1 validators ([LicenseStatusValidator.cs](../../apps/api/Application/Scoring/Validators/LicenseStatusValidator.cs), [DeaStatusValidator.cs](../../apps/api/Application/Scoring/Validators/DeaStatusValidator.cs), [BoardCertificationValidator.cs](../../apps/api/Application/Scoring/Validators/BoardCertificationValidator.cs)) all short-circuit to `Array.Empty<Issue>()` when their sub-record is null **specifically** to defer to this aggregator-level Critical and avoid double-counting. Same contract for `MalpracticeCurrencyValidator` when it ships in P4.
- **`RequiredDocumentsValidator` owns Missing-Document Critical for payer-required doc types NOT in the universal-4** (e.g., a future `StateRegistration` or `CDS` doc). It MUST skip emission for any doc type already covered by the aggregator — enforce in code (`UNIVERSAL_DOC_TYPES.Contains(t)` skip), not by coincidence. With the two YAMLs in P4 (both require only the universal-4), this validator emits nothing in the common case — it's a forward-compatibility lane for payer #3+.
- **Extraction-Failed Critical** (status=`'Failed'`) stays with the aggregator regardless of payer config.

So the validator's job is: read `requiredDocuments: [...]` from the payer's YAML, subtract the universal-4, walk the provider's extractions for what's left, emit one Critical per missing non-universal doc type. Citations are doc-less (`null` doc-ref fields) — there's nothing to point at when the doc isn't there.

The earlier draft's `PayerSpecificValidator` was a junk drawer — required docs + accepted boards + windows all in one place. Split per concept: required-docs is its own thing (scoped as above); accepted-boards extends the existing `BoardCertificationValidator`; license/dea renewal windows stay in their respective validators with payer-override hooks.

### `apps/api/Application/Scoring/Validators/BoardCertificationValidator.cs` (existing, extended)

Phase 1 shipped this with presence/status/expiry checks. P4 extends it to consume optional payer config:

- If `payer.boardCertRequired == false`, "no board cert on file" downgrades from Critical to Pass (don't emit an Issue at all).
- If `payer.acceptedBoards` is non-empty and the extracted board isn't in the list, emit Major "Board {X} not on the accepted list for {payer}; payer accepts {list}".

Other behavior unchanged. The change is **additive** behind a "payer config present?" check; passing no payer config keeps current behavior. Phase 1 tests stay green.

### `apps/api/Infrastructure/Payers/PayerRequirementLoader.cs`

Loads all `payers/*.yaml` at startup. Failure-mode: missing file referenced by a profile's `payerId` → fail-loud at startup, not on first request.

### `apps/api/Infrastructure/Payers/payers/payer-a-national-hmo.yaml`

```yaml
id: payer-a
name: Payer A — National HMO
malpractice:
  minimumPerOccurrence: 1000000   # $1M
  minimumAggregate: 3000000       # $3M
requiredDocuments:
  - license
  - dea
  - boardCert
  - malpractice
boardCertRequired: true           # some payers don't require board cert
acceptedBoards:
  - ABMS                          # American Board of Medical Specialties
windowDays:
  malpracticeRenewal: 30
  licenseRenewal: 30
```

`payer-b-state-medicaid.yaml` is structurally identical with different minimums and a `boardCertRequired: false` to exercise that branch.

### `apps/api/Application/Scoring/ConfidenceGuard.cs`

```csharp
public static class ConfidenceGuard
{
    public const double CriticalEligibleThreshold = 0.85;

    /// <summary>
    /// Pure fold over the issue list. Downgrades any Critical Issue whose
    /// citations carry <c>LowConfidence == true</c> to Minor and stamps
    /// <c>IsLowConfidenceInput = true</c> on the returned Issue. No
    /// provenance lookup happens here — <c>ProvenanceExtensions.Cite</c>
    /// already set <c>Citation.LowConfidence</c> at emission time using
    /// <see cref="FieldProvenance.Confidence"/> &lt;
    /// <see cref="CriticalEligibleThreshold"/>.
    ///
    /// Idempotent: re-running sees the Criticals already downgraded;
    /// nothing further to do.
    /// </summary>
    public static IReadOnlyList<Issue> Apply(IReadOnlyList<Issue> issues) =>
        issues.Select(Downgrade).ToList();

    private static Issue Downgrade(Issue i) =>
        i.Severity == Severity.Critical && i.Citations.Any(c => c.LowConfidence)
            ? i with { Severity = Severity.Minor, IsLowConfidenceInput = true }
            : i;
}
```

Called from `ComputeReadinessScoreCommandHandler` between issue collection and `ScoreSynthesizer.Compute`.

**`Issue.IsLowConfidenceInput` and `Citation.LowConfidence` already shipped in P3** — see [Issue.cs](../../apps/api/Domain/Scoring/Issue.cs) and [Citation.cs](../../apps/api/Domain/Scoring/Citation.cs). Both default `false` and stay unflipped until P4 wires the consumer. STJ + the camelCase policy serialize them as `isLowConfidenceInput` and `lowConfidence` automatically. P1/P2/P3 callers and tests are unchanged.

**Where `Citation.LowConfidence` gets flipped:** in [`ProvenanceExtensions.Cite`/`TryCite`](../../apps/api/Application/Providers/Aggregation/ProvenanceExtensions.cs). P4 extends these to read `FieldProvenance.Confidence` and stamp `LowConfidence = (confidence < CriticalEligibleThreshold)` on every citation they construct. Validators already call `provenance.Cite(...)` to build citations — no validator-code change needed for the flip to land everywhere. LLM validators that build `Citation` directly (e.g., `IdentityCoherenceValidator`) must use the same `Cite` helper or pass the resolved `FieldProvenance` through explicitly so they participate in the gate.

The dashboard's IssueCard checks `IsLowConfidenceInput` and renders an inline "low-confidence input" pill in the severity row; the side-panel says "downgraded from Critical due to low-confidence input."

### `evals/runners/conflict_metrics.py`

```python
@dataclass
class ConflictCount:
    kind: str               # "name_variant" | "taxonomy_specialty_mismatch"
    planted: int            # how many of this kind in the dataset
    caught: int             # planted AND matched by an Issue from the expected validator
    fabricated: int         # validator flagged a conflict on a clean packet (no planted entry)

EXPECTED_VALIDATOR = {
    "name_variant":                "identity_coherence",
    "taxonomy_specialty_mismatch": "npi_taxonomy_match",
}

def measure(packet_results: list[PacketResult]) -> dict[str, ConflictCount]:
    """
    A planted conflict is 'caught' iff ALL THREE:
      1. At least one Issue's `validator` equals EXPECTED_VALIDATOR[kind].
      2. The Issue's citations name at least one of the planted `sources`.
      3. The Issue's `field` equals the planted conflict's target field
         (identity_coherence emits a `field` discriminator; name_variant
         expects `fullName`, taxonomy_specialty_mismatch expects `specialty`).
    The third predicate prevents a "right validator, wrong finding" from
    counting as a catch — e.g. identity_coherence noticing a DOB drift on
    the (license, malpractice) pair when we planted a name_variant.

    Fabrications are counted on packets with `plantedConflicts == []`.
    """
```

Per-kind precision/recall reported in `baseline.json` under a `conflicts` key. `expiry_mismatch` is *not* in `EXPECTED_VALIDATOR` — see "Conflict kinds in P4" decision; that kind lands in a Phase 4.5 follow-on.

### `evals/runners/agreement.py`

Replaces the earlier `correlation.py` plan — Spearman is wrong for 3-tier categorical labels at n=20 (ties dominate; ρ destabilizes; p-values become uninterpretable). Quadratic-weighted Cohen's κ is the standard ordinal-categorical agreement metric.

```python
import numpy as np
from scipy.stats import spearmanr  # kept only for the footnote

TIER_TO_ORD = {"Red": 0, "Yellow": 1, "Green": 2}

@dataclass
class AgreementMetrics:
    weighted_kappa: float        # quadratic weights; headline number
    raw_agreement: float         # count(system_tier == human_tier) / n
    confusion_3x3: list[list[int]]  # rows = human tier, cols = system tier
    spearman_rho: float          # footnote only — score (continuous) vs human tier (ordinal)
    n: int

def measure(
    score_results: dict[str, ScoreResult],
    labels: dict[str, str],   # packet_id → "Red" | "Yellow" | "Green"
) -> AgreementMetrics:
    """
    Headline: quadratic-weighted Cohen's κ. Disagreement weights scale as
    (distance / max_distance)² — Red→Yellow misses are penalized less than
    Red→Green. Substantial-agreement floor (Landis-Koch) is 0.61; the P4 DoD
    sets the floor at 0.50 because labeler is solo and the structural bias
    (validator-designer-also-labels) means κ overstates ground-truth tracking.
    """
```

`baseline.json` carries the new shape under an `agreement` key:

```json
"agreement": {
  "weightedKappa": 0.0,
  "rawAgreement": 0.0,
  "confusion3x3": [[0,0,0],[0,0,0],[0,0,0]],
  "spearmanRho": 0.0,
  "n": 20
}
```

### `evals/labels/human_tiers.json`

```json
{
  "_method": "Read each packet's PDFs without looking at PacketReady's computed score. Rate the provider's submission readiness as Red / Yellow / Green using the same rubric a credentialing admin would: critical blockers → Red; significant issues that need attention → Yellow; ready to submit → Green.",
  "_labeler": "Ben (solo for P4 — see README caveat)",
  "_biasNote": "The labeler is also the designer of the validator suite. The agreement number this set produces measures self-consistency between the rules-in-the-validators and the rules-in-the-labeler's-head — NOT ground truth. A reader looking for 'does this system match an independent expert' should treat the κ as an upper-bound estimate. The README's accuracy section names this in plain language.",
  "_date": "2026-MM-DD",
  "labels": {
    "packet-001-clean-anderson": "Green",
    "packet-002-clean-bautista": "Green",
    "...": "20 entries total"
  }
}
```

Labels are written **before** the eval runs against this dataset — so the labeler isn't anchored on PacketReady's output. Mechanically enforced: the runner refuses to compute agreement metrics if `human_tiers.json`'s mtime is later than the corresponding `baseline.json`'s `generatedAt`.

The mtime check guards against **anchoring** (rating after seeing the system score). It does NOT guard against the **structural** bias above: same person who designed the validators is rating readiness. The bias survives any anchoring discipline. The fix is a second labeler from outside; until then, name the bias explicitly so a reader knows what the number does and doesn't claim.

---

## Task order

1. **Confirm per-field confidence wiring (P3-shipped; no migration).** Verify the `confidence` JSONB column on `document_extractions` ([DocumentExtractionConfiguration.cs](../../apps/api/Infrastructure/Persistence/Configurations/DocumentExtractionConfiguration.cs)), `FieldProvenance.Confidence` ([FieldProvenance.cs](../../apps/api/Application/Providers/Aggregation/FieldProvenance.cs)), and the default-`false` `Issue.IsLowConfidenceInput` / `Citation.LowConfidence` ([Issue.cs](../../apps/api/Domain/Scoring/Issue.cs), [Citation.cs](../../apps/api/Domain/Scoring/Citation.cs)) are all present. The actual P4 work for confidence is two surgical changes in step 14: extend `ProvenanceExtensions.Cite` to stamp `Citation.LowConfidence`, then add `ConfidenceGuard.Apply` in the handler.
2. **NUCC + NPPES data snapshots committed** under `data/`. README in that dir names the file source and the snapshot date.
3. **Add `PayerId` to `Provider`.** Column + migration; defaults to `payer-a-national-hmo`. Seed CLI sets per fixture (roughly half each payer across the 50).
4. **`nppes_sampling.py` + seeded `faker`.** Smoke: generate 5 profiles, eyeball the distribution.
5. **`conflict_planters.py` for 2 kinds: `name_variant`, `taxonomy_specialty_mismatch`.** Each planter writes its own `plantedConflicts` entry with `kind`, `field`, `sources`, `expectedSeverity`. Unit tests on each planter: mutation correctness + golden.json consistency + PDF rendering still works.
6. **`packets.py` generates 50 packets.** Open 3 random packets (one from each non-trivial bucket) and verify visually.
7. **Per-payer YAML schema lock + 2 payers + `PayerRequirementLoader`.** Fail-loud at startup on schema violation or unreferenced `payerId`.
8. **`IdentityCoherenceValidator` + prompt** wired in DI. **Cherry-pick a 10-packet tuning subset** (5 clean + 5 with planted `name_variant`) — *all* prompt tuning happens on this subset, NOT the full 50. Cuts prompt-iteration cost roughly 3× ($30/iteration on the 10-set vs $90+ on the 50-set).
9. **Tune `IdentityCoherenceValidator` prompt on the 10-subset until FP rate < 5%** (zero fabrications on the 5 clean packets). Recall is secondary — don't co-optimize. If FP < 5% costs recall < 80%, the prompt is wrong, not the threshold.
10. **`NpiTaxonomyMatchValidator`: CSV lookup helper + thin LLM compare prompt.** Same 10-subset tuning discipline (5 clean + 5 with `taxonomy_specialty_mismatch`).
11. **`MalpracticeCurrencyValidator`** — pure code, consumes payer config for coverage minimums + window.
12. **`RequiredDocumentsValidator`** — pure code, single responsibility (per-payer required-doc presence).
13. **Extend `BoardCertificationValidator`** with the optional payer-config branch (`boardCertRequired: false` downgrades the missing-cert Critical to Pass; non-accepted board emits Major).
14. **`ConfidenceGuard` + handler integration.** Extend `ProvenanceExtensions.Cite`/`TryCite` to stamp `Citation.LowConfidence` from `FieldProvenance.Confidence < 0.85`. Add `ConfidenceGuard.Apply` and call it from `ComputeReadinessScoreCommandHandler` between issue collection and `ScoreSynthesizer.Compute`. Unit-test the downgrade path: a Critical with one low-conf citation becomes Minor with `IsLowConfidenceInput = true`; the same Critical with all citations ≥0.85 stays Critical. (`Issue.IsLowConfidenceInput` and `Citation.LowConfidence` already shipped in P3 — no record changes.) Verify the existing P1 IssueCard renders the pill.
15. **`conflict_metrics.py`** + runner wiring. Smoke against 3 planted packets, confirm recall = 100% on that micro-set with the 3-predicate definition (validator + sources + field).
16. **Hand-label 20 packets into `human_tiers.json`** — done *before* step 17 to avoid anchoring. Write the `_biasNote` block in the same commit.
17. **`agreement.py`** + runner wiring (weighted κ + raw + 3×3 confusion; Spearman as footnote).
18. **First full P4 eval run against the 50-packet set.** Use the final prompts. Pipe results into `evals/results/latest.json`.
19. **Verify Appendix A competitor comparison rows.** Open each competitor's site and re-confirm the row's claims; soften any inferred-from-marketing language to "based on publicly marketed features as of YYYY-MM". Better no claim than a wrong one — the README publishes this table.
20. **Commit `evals/results/baseline.json`** with `stub: false` and the locked numbers.
21. **Update README** with accuracy table, conflict precision/recall, agreement metrics (κ headline, confusion matrix below), the verified competitor row, and the labeler-bias paragraph in plain language.
22. **Gate verification.** Walk the 10 DoD checkboxes.

Order matters: 1 unblocks 14; 2+7 unblock 10; 3 unblocks 11+12+13; 4 unblocks 5; 5 unblocks 6; 6+7 unblock 8+10; 8+10 unblock 15; 11+12+13 unblock 18; 16 must precede 17; 18 unblocks 19+20; 20 must follow 9 and 10's tuning.

---

## Risks / open

- **Hand-labeler bias is structural, not just mechanical.** The validator designer is also the labeler. The pre-eval mtime check protects against *anchoring* (seeing the system score first), but not against the deeper problem: the labeler is mentally simulating the validators while rating. The agreement number measures self-consistency between the rules-in-the-validators and the rules-in-the-labeler's-head — **not** ground truth. README must say this in plain language, and the `_biasNote` field in `human_tiers.json` carries it in-band. The honest fix is a second labeler from outside credentialing; we're shipping without one and naming it.
- **Sample size for κ.** n=20 is small. The 0.50 floor in DoD reflects what's achievable given the structural bias; the design-doc Spearman target (0.80) was implicitly assuming continuous-on-both-sides and is replaced. Don't tune the system to maximize κ — overfitting to 20 labels is a textbook trap.
- **Conflict-recall vs FP tradeoff on LLM validators.** Tightening the prompt to drive FP < 5% will cost recall. P4 commits to FP < 5% as the gate; recall ≥ 80% is the secondary target. If both can't be hit simultaneously, ship FP-tight and call out the recall ceiling in the README.
- **NPPES sample staleness.** The snapshot is from 2026-Q2; specialty distributions don't shift fast but they shift. Treat the snapshot as immutable for P4; refresh in a future P6 follow-on if licensure patterns visibly drift.
- **YAML schema as contract.** Two payers in P4 means we lock in a schema before payer #3 surfaces a counterexample. Mitigation: keep the schema minimal — required documents, malpractice minimums, accepted boards, expiry windows. Anything fancier is a P5+ negotiation.
- **Cost per FULL eval run vs PROMPT-TUNING run.** Full eval (50 packets × ~4 docs × Sonnet vision × ~$0.02) + LLM validators ≈ **~$6/run** — that's the CI/regression cost, OK to run on prompt/model PRs. **Prompt tuning costs more.** An FP-rate measurement of `identity_coherence` against 30 conflict-free packets at ~$0.04/call ≈ $1.20/measurement; ten tuning iterations on the full set ≈ $12 — fine, but extrapolating to the 50-set with multi-tier checks pushes higher. Task 8 mitigates: tune on a **10-packet subset** (5 conflict-free + 5 planted) and final-validate on the full 50 once. Cuts tuning cost ~3×.
- **Confidence emission depends on P3.** If P3's extractor prompts don't ask Sonnet for per-field confidence, step 1 is genuinely a P3 patch wearing P4 clothing — extractor prompt change + migration + schema flow-through, not just a column add. Confirm before scoping P4 start.
- **Hand-labeling 20 packets blocks agreement metrics.** ~100 minutes of focused reading; the runner can be written in parallel, but the first agreement number can't compute until labels exist.
- **PDF byte-stability is fragile.** Python `random` + `faker` + numpy sampling are deterministic. ReportLab embeds PDF creation timestamps and document IDs by default — pin both, or accept that PDF bytes vary per run. PyMuPDF's rasterization for the scanned bucket has platform-dependent JPEG quantization. **The claim is "deterministic field values + locked seeds for the data pipeline"; the PDF bytes themselves are *visually* identical, not bytewise.** A regen-twice-and-diff test catches the obvious drift; ship that as a smoke test before the dataset is checked in.
- **Competitor-comparison claims in design.md Appendix A.** Earlier review flagged inferred-from-marketing entries (Verifiable's intake breadth, Assured's pre-submission positioning, Medallion's enterprise focus). The README publishes the table. Task 19 verifies each row against the current site before P4 closes; soften any unverifiable claims to "based on publicly marketed features as of 2026-MM". A wrong row there is worse than no row.

---

## Out of scope (resist)

- **A third LLM validator.** `address_drift`, `dob_mismatch`, and friends wait for a Phase 4.5 once the two existing LLM validators are stable. Three LLM validators co-evolving = three prompts you can't tune independently.
- **`ExpiryConsistencyValidator` (the missing piece for `expiry_mismatch`).** Cross-document date-disagreement detection is real work — its own prompt, its own field discriminator, its own FP-tuning curve. **Lands in Phase 4.5**, after the two existing LLM validators are stable and tunable independently. Until then, `expiry_mismatch` is NOT a planted kind in P4 and NOT measured.
- **More than 2 payers in YAML.** Two is enough to exercise both branches (board-cert-required and board-cert-optional). Payer #3 lands when a real customer asks.
- **Payer selection at intake.** P4's `PayerId` defaults at `Provider` creation; admin-driven payer choice surfaces in the intake portal in P5.
- **Continuous integration of the regression gate.** Gate runs locally + on prompt/model change PRs. CI integration is P6.
- **Real-time score recompute on extraction change.** The "drop a new PDF → score updates" UX is the intake-portal flow in P5. P4 still uses the seed CLI + P3 upload flow.
- **Intake agent, outbox, magic-link portal.** P5.
- **A real second labeler.** Name the bias; don't block the phase.
- **Bbox-quality metrics.** P3 reports bbox; we don't measure its accuracy in P4 (would require ground-truth bboxes on every field × every doc, which is days of labeling). The "dashboard highlights the right region" gate is human-eyeballed in P6 demo polish.
- **Fancy conflict-detection metrics** (F1 per kind, AUC over confidence thresholds). Precision + recall per kind is the contract; everything else is P6.

---

## What gets written when Phase 4 closes

Append a one-line outcome note to [build-plan.md](../build-plan.md) Status. Then write `phase-5-intake-agent.md`. Topics: `IntakeSession` FSM (port from VaBene `OnboardingSession`), `IntakeAgent` with the 5 tools, `IntakeTurnJob` (Hangfire with `FOR UPDATE`), outbox table + 10-min hold-at-send TTL, magic-link portal (Next.js, single page), mock SMTP, per-provider turn budget cap.

The hand-off point for Atano outreach is here, not P5. **After P4 closes, the README quotes real accuracy numbers + the dashboard demos against a 50-packet dataset.** That's the credible artifact. P5 + P6 expand the surface, but they don't move the central claim.
