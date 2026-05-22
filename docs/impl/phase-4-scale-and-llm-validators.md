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

- [ ] `evals/dataset/` holds **50 packets** in 4 buckets: 15 clean+valid · 15 clean+conflicts · 15 scanned+clean · 5 scanned+conflicts. Generated programmatically from NPPES distributions with a locked random seed; regen is byte-stable across machines pinned to the same Python deps.
- [ ] **Per-field confidence** lands on every extraction row (added in P3 if not already; P4 step 0 if not). Float in `[0, 1]`. Surfaced through to the score path.
- [ ] **Two new LLM-augmented validators wired:** `identity_coherence` (Sonnet structured output across all extractions for one provider) and `npi_taxonomy_match` (Sonnet + NUCC taxonomy snapshot). Both registered in `AddApplication()` and exercised by `ComputeReadinessScoreCommand`.
- [ ] **Two new pure-code validators wired:** `malpractice_currency` (in-force + coverage ≥ payer minimum + ≥ 30-day window) and `payer_specific` (YAML-driven; two sample payers committed).
- [ ] **Conflict precision + recall** reported per `plantedConflict` kind. Definition: a planted conflict is "caught" iff at least one Issue's `Validator` matches the expected validator for that kind AND the Issue's citations name at least one of the planted `sources`. Precision = caught-and-planted / all-flagged-conflicts; recall = caught-and-planted / total-planted.
- [ ] **Spearman score correlation ≥ 0.65** against 20 hand-labeled tiers (target ≥ 0.80 per [design.md §3.1](../design.md), realistic floor at n=20 is 0.65). Labels live at `evals/labels/human_tiers.json`, written **before** running the eval so the labeler isn't anchored on system output.
- [ ] **Confidence-threshold gate** active: a Critical Issue whose citations include any field with confidence < 0.85 is downgraded to Minor with a `lowConfidenceInput` marker. Documented in [design.md §11.1](../design.md).
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
| Conflict kinds in P4 | `name_variant`, `expiry_mismatch`, `taxonomy_specialty_mismatch` (3 total) | Covers both LLM validators + reuses P2's planters. Avoids the "add five conflict types and tune them all at once" failure mode. |
| Hand-label process | Tiers (Red/Yellow/Green) only, not numeric scores | Numeric labels invite spurious precision at n=20. Categorical labels are what humans actually agree on. |
| Hand-labeler | Solo (Ben) for P4; flagged caveat in README | A second labeler is ideal; deferring to "find a second" would block the phase indefinitely. The label-noise caveat is the honest answer. |
| Per-field confidence column | New column on `document_extractions` (`confidences` JSONB, keyed by field name) | Keeps the row shape stable; doesn't require a sibling table. P3 may have already added this — confirm in step 0. |
| Per-payer YAML location | `apps/api/Infrastructure/Payers/payers/*.yaml`, loaded at startup | One restart picks up changes. P5+ may need hot-reload; defer. |
| Confidence threshold | 0.85 for Critical-eligible inputs | Inherited from [design.md §11.1](../design.md); revisit only with data showing it's wrong. |

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
│       │   │   ├── MalpracticeCurrencyValidator.cs      NEW (pure)
│       │   │   └── PayerSpecificValidator.cs            NEW (pure, YAML-driven)
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
│   ├── nucc-taxonomy-snapshot-2026-Q2.csv               NEW — official NUCC
│   └── nppes-sample-2026-Q2.csv                         NEW — sampled subset
└── evals/
    ├── dataset/
    │   ├── packet-001 … packet-005                       (P2; rebucketed under P4 IDs)
    │   └── packet-006 … packet-055                       NEW — programmatic
    ├── generators/packetready_eval/
    │   ├── nppes_sampling.py                             NEW
    │   ├── conflict_planters.py                          NEW — taxonomy_specialty_mismatch joins name_variant + expiry_mismatch
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

### `data/nucc-taxonomy-snapshot-2026-Q2.csv`

The official NUCC code set, downloaded once and committed. ~900 rows; treat as static data. Future taxonomy code edits go via a new snapshot file with a new quarter suffix — same pattern as `evals/results/`.

### `data/nppes-sample-2026-Q2.csv`

A 10k-row sample of NPPES providers downloaded once. Columns we need: NPI, primary specialty, taxonomy code, license issuance state, license issuance year. Sampled, not the full 7M-row dump — keeps the repo light. The packet generator reads this file and picks rows uniformly with a locked seed.

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
    """license: 'Jane Calloway'; malpractice: 'Jane C. Calloway-Smith'."""

def plant_expiry_mismatch(spec: PacketSpec, rng: Random) -> None:
    """license.pdf expiry vs malpractice.pdf Licensee-footer expiry."""

def plant_taxonomy_specialty_mismatch(spec: PacketSpec, rng: Random) -> None:
    """NPI taxonomy code on license = Cardiology; board cert specialty = Family Medicine."""
```

Each planter also writes the `plantedConflicts` entry — the runner reads this to score recall.

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

Same shape as `IdentityCoherenceValidator`. Loads NUCC taxonomy at startup; passes the candidate taxonomy code + the stated specialty to Sonnet; structured output says either `match: true` or returns a Major Issue with the closest valid specialty for that code.

### `apps/api/Application/Scoring/Validators/MalpracticeCurrencyValidator.cs`

Pure code. Three checks per malpractice extraction:
- Status == `Active` (Critical if not).
- Coverage limits ≥ the payer's required minimum (Major if below, **per credentialing target's payer**; needs payer context plumbed from `ProviderProfile.payerId`).
- Expiry ≥ today + 30 days (Minor inside the window, Critical past expiry — already covered by an existing `MalpracticeExpiryValidator`? Confirm during execution; merge or leave separate).

If `ProviderProfile.PayerId` doesn't exist yet, P4 adds it (one new field on the profile; P3-emitted profile gains it from the upload context).

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
    /// Downgrades any Critical Issue whose citations reference a field with
    /// confidence &lt; <see cref="CriticalEligibleThreshold"/> to Minor.
    /// Annotates the downgraded Issue with a marker so the dashboard can
    /// surface "we caught this but the input was unreliable" without
    /// hiding the finding entirely.
    /// </summary>
    public static IReadOnlyList<Issue> Apply(
        IReadOnlyList<Issue> issues,
        IReadOnlyDictionary<(Guid docId, string field), double> confidences)
    {
        // ...
    }
}
```

Called from `ComputeReadinessScoreCommandHandler` between collection and sort. The marker is a string suffix on `Issue.Message` — `(low-confidence input)` — and a flag on `Citation.lowConfidence: bool` so the dashboard can render it.

### `evals/runners/conflict_metrics.py`

```python
@dataclass
class ConflictCount:
    kind: str               # "name_variant" | "expiry_mismatch" | "taxonomy_specialty_mismatch"
    planted: int            # how many of this kind in the dataset
    caught: int             # planted AND matched by an Issue from the expected validator
    fabricated: int         # validator flagged a conflict on a clean packet (no planted entry)

def measure(packet_results: list[PacketResult]) -> dict[str, ConflictCount]:
    """
    A planted conflict is 'caught' iff:
      1. At least one Issue's `validator` equals the expected validator for `kind`
         (identity_coherence for name_variant/expiry_mismatch on a per-field axis,
          npi_taxonomy_match for taxonomy_specialty_mismatch).
      2. The Issue's citations include at least one of the planted `sources`.
    Fabrications are counted on packets with `plantedConflicts == []`.
    """
```

Per-kind precision/recall reported in `baseline.json` under a `conflicts` key.

### `evals/runners/correlation.py`

```python
def spearman_against_labels(
    score_results: dict[str, ScoreResult],
    labels: dict[str, Tier],
) -> tuple[float, float]:
    """
    Returns (rho, p_value). Tier mapping: Red=0, Yellow=1, Green=2.
    n=20 means the p-value is informational; the rho is the headline.
    """
```

### `evals/labels/human_tiers.json`

```json
{
  "_method": "Read each packet's PDFs without looking at PacketReady's computed score. Rate the provider's submission readiness as Red / Yellow / Green using the same rubric a credentialing admin would: critical blockers → Red; significant issues that need attention → Yellow; ready to submit → Green.",
  "_labeler": "Ben (solo for P4 — see README caveat)",
  "_date": "2026-MM-DD",
  "labels": {
    "packet-001-clean-anderson": "Green",
    "packet-002-clean-bautista": "Green",
    ...20 entries total
  }
}
```

Labels are written **before** the eval runs against this dataset — so the labeler isn't anchored on PacketReady's output. Mechanically enforced: the runner refuses to compute correlation if `human_tiers.json`'s mtime is later than the corresponding `baseline.json`'s `generatedAt`.

---

## Task order

1. **Confirm or add per-field confidence on extractions.** If P3 didn't emit it, step 0 is a small migration (`confidences JSONB` on `document_extractions`) + extractor prompt update to ask Sonnet for self-rated 0–1 confidence per field.
2. **NUCC + NPPES data snapshots committed** under `data/`. README in that dir names the file source and the snapshot date.
3. **`nppes_sampling.py` + seeded `faker`.** Smoke: generate 5 profiles, eyeball the distribution.
4. **`conflict_planters.py` for 3 kinds.** Unit tests on each planter: it mutates a spec, the resulting `plantedConflicts` matches what was planted, and the PDF rendering still works.
5. **`packets.py` generates 50 packets.** Spot-check: open 3 random packets (one from each non-trivial bucket).
6. **`IdentityCoherenceValidator` + prompt** wired in DI. Smoke against a clean packet (should emit zero Issues) and a planted-`name_variant` packet (should emit Critical with both source citations).
7. **`NpiTaxonomyMatchValidator` + prompt + NUCC lookup.**
8. **Per-payer YAML schema lock + 2 payers + `PayerRequirementLoader`.**
9. **`MalpracticeCurrencyValidator` consumes payer config.**
10. **`PayerSpecificValidator` consumes payer config.**
11. **`ConfidenceGuard` + handler integration.** Unit-test the downgrade path explicitly.
12. **`conflict_metrics.py`** + runner wiring. Smoke against 3 planted packets, confirm recall = 100% on that micro-set.
13. **Hand-label 20 packets into `human_tiers.json`** — done *before* step 14 to avoid anchoring.
14. **`correlation.py` + runner wiring.**
15. **First full P4 eval run.** Pipe results into `evals/results/latest.json`.
16. **Tune `identity_coherence` prompt until FP rate < 5%** on the 30 conflict-free packets. Recall comes later — don't co-optimize.
17. **Tune `npi_taxonomy_match` prompt** to the same bar.
18. **Commit `evals/results/baseline.json`** with `stub: false` and the locked numbers.
19. **Update README** with accuracy table, conflict precision/recall, Spearman, competitor row.
20. **Gate verification.**

Order matters: 1 unblocks 11; 2 unblocks 7; 4 unblocks 5; 6+7 unblock 12; 8 unblocks 9+10; 13 must precede 14; 15 unblocks 16+17; 18 must follow tuning.

---

## Risks / open

- **Hand-labeler bias.** The validator designer (Ben) labels — labels and validators aren't independent. The Spearman number measures "system tracks the designer's intuition," which is weaker than "system tracks ground truth." README must say this in plain language. A second labeler before launch is ideal; not gating the phase on finding one.
- **Sample size for Spearman.** n=20 is small. The 0.65 floor in DoD reflects what's achievable; the 0.80 design-doc target is a stretch. Don't tune the system to maximize Spearman — overfitting to 20 labels is a textbook trap.
- **Conflict-recall vs FP tradeoff on LLM validators.** Tightening the prompt to drive FP < 5% will cost recall. P4 commits to FP < 5% as the gate; recall ≥ 80% is the secondary target. If both can't be hit simultaneously, ship FP-tight and call out recall ceiling in the README.
- **NPPES sample staleness.** The snapshot is from 2026-Q2; specialty distributions don't shift fast but they shift. Treat the snapshot as immutable for P4; refresh in a future P6 follow-on if licensure patterns visibly drift.
- **YAML schema as contract.** Two payers in P4 means we lock in a schema before payer #3 surfaces a counterexample. Mitigation: keep the schema minimal — required documents, malpractice minimums, expiry windows. Anything fancier is a P5+ negotiation.
- **Cost per eval run.** Back-of-envelope: 50 packets × ~4 docs × Sonnet vision extraction × ~$0.02 ≈ $4 + identity_coherence ≈ $1 + npi_taxonomy_match ≈ $1 ≈ **~$6 per full eval**. Don't auto-run on every PR; the regression gate runs in CI on prompt/model changes, not on every diff.
- **Confidence emission depends on P3.** If P3's extractor prompts don't ask Sonnet for per-field confidence, step 1 is genuinely a P3 patch — the chain `Sonnet → extraction row → ConfidenceGuard` is broken without it. Confirm before scoping.
- **Hand-labeling 20 packets blocks the runner upgrade.** ~100 minutes of focused reading; the runner can be written in parallel, but the first full eval can't compute Spearman until labels exist.

---

## Out of scope (resist)

- **A third LLM validator.** `address_drift`, `dob_mismatch`, and friends wait for a Phase 4.5 once the two existing LLM validators are stable. Three LLM validators co-evolving = three prompts you can't tune independently.
- **More than 2 payers in YAML.** Two is enough to exercise both branches (board-cert-required and board-cert-optional). Payer #3 lands when a real customer asks.
- **Continuous integration of the regression gate.** Gate runs locally + on prompt/model change PRs. CI integration is P6.
- **Real-time score recompute on extraction change.** The "drop a new PDF → score updates" UX is the intake-portal flow in P5. P4 still uses the seed CLI + P3 upload flow.
- **Intake agent, outbox, magic-link portal.** P5.
- **A real second labeler.** Acknowledge the bias; don't block the phase.
- **Bbox-quality metrics.** P3 reports bbox; we don't measure its accuracy in P4 (would require ground-truth bboxes on every field × every doc, which is days of labeling). The "dashboard highlights the right region" gate is human-eyeballed in P6 demo polish.
- **Fancy conflict-detection metrics** (F1 per kind, AUC over confidence thresholds). Precision + recall per kind is the contract; everything else is P6.

---

## What gets written when Phase 4 closes

Append a one-line outcome note to [build-plan.md](../build-plan.md) Status. Then write `phase-5-intake-agent.md`. Topics: `IntakeSession` FSM (port from VaBene `OnboardingSession`), `IntakeAgent` with the 5 tools, `IntakeTurnJob` (Hangfire with `FOR UPDATE`), outbox table + 10-min hold-at-send TTL, magic-link portal (Next.js, single page), mock SMTP, per-provider turn budget cap.

The hand-off point for Atano outreach is here, not P5. **After P4 closes, the README quotes real accuracy numbers + the dashboard demos against a 50-packet dataset.** That's the credible artifact. P5 + P6 expand the surface, but they don't move the central claim.
