# Phase 2 — Eval Harness + 5 Packets

> A regression suite for extractor accuracy, built before any extractor exists. The harness is the artifact; the accuracy table will be zeros until Phase 3 lands.

| | |
|---|---|
| **Parent** | [build-plan.md](../build-plan.md) — Phase 2 row |
| **Goal** | The harness exists. Every Phase 3+ extractor change runs through it and is measured. |
| **Status** | Not started |
| **Data** | synthetic PDFs + golden JSON labels · no PHI · operator-only |
| **Depends on** | [Phase 1](./phase-1-score-from-clean-input.md) — closed 2026-05-21 |
| **Style** | [../style.md](../style.md) |

---

## Definition of done

- [ ] `evals/dataset/` holds 5 hand-curated synthetic packets, each in its own directory.
- [ ] Each packet has 4–5 PDFs (license / DEA / board cert / malpractice / [CV]) and a `golden.json` with every field's expected value.
- [ ] Packet mix: **2 clean + valid**, **2 clean + planted conflicts** (one name-variant, one expiry-mismatch), **1 scanned-style clean** (rasterized + skew + JPEG noise).
- [ ] `evals/runners/run.py` runs end-to-end. With no extractor wired (P3 problem), it produces an all-zeros accuracy table and writes `evals/results/<ISO-timestamp>.json` — the harness is what's being validated, not the accuracy.
- [ ] At least one planted-conflict packet has been hand-verified — the file claims one thing and the planted-conflict marker in `golden.json` is recoverable from the PDFs by a human reader.
- [ ] Regression gate: `python evals/runners/run.py --check-against evals/results/baseline.json` exits non-zero if any per-field metric drops > 2 pp from the baseline.

All six boxes check → Phase 2 closes. Move to [Phase 3 — Extractors + classifier](./phase-3-extractors.md).

---

## Stack additions

| Layer | Addition | Why |
|---|---|---|
| Python 3.11+ | `evals/generators/` package | PDF generation. ReportLab is Python-only, runner colocates to avoid cross-process plumbing. |
| ReportLab | latest | Per-doc-type PDF layouts. Picked over LaTeX (heavier, slower to iterate) and `pdf-lib` JS (worse layout primitives). |
| Pillow + PyMuPDF | latest | Rasterize the scanned packet; apply skew/JPEG/noise. |
| pyproject.toml + venv | — | Standard Python project layout, no Poetry/Pipenv. |

Backend: **no new packages.** P2's API surface is the placeholder extractor stub, which is a single endpoint that returns empty extractions until P3.

---

## Decisions baked in (from build-plan.md "Open decisions")

| Decision | Choice | Why locked here |
|---|---|---|
| PDF generator toolchain | ReportLab (Python) | Best layout control of the scriptable options. Locked in P2; can't easily change in P4 once 50 packets exist. |
| Eval runner language | Python | Colocates with the generator (no cross-process build step). Lower friction for P4 metric extensions. |

---

## Project layout deltas

```
PacketReady/
├── apps/
│   └── api/
│       └── Application/Extraction/             NEW (P2 stub; P3 fills it)
│           └── ExtractDocumentEndpoint.cs       returns empty extraction
├── evals/
│   ├── fixtures/                                (existing — P1 provider profiles)
│   ├── dataset/                                 NEW
│   │   ├── packet-001-clean-anderson/
│   │   │   ├── license.pdf
│   │   │   ├── dea.pdf
│   │   │   ├── board-cert.pdf
│   │   │   ├── malpractice.pdf
│   │   │   └── golden.json
│   │   ├── packet-002-clean-bautista/           (NY → CA, internal med → cardiology)
│   │   ├── packet-003-conflict-name/            (license: "Jane Calloway", malpractice: "Jane C. Calloway-Smith")
│   │   ├── packet-004-conflict-expiry/          (license PDF says 2027-03-15, golden says 2026-03-15)
│   │   └── packet-005-scanned-anderson/         (rasterized clone of 001 at 200dpi + 0.7° skew + JPEG q=70)
│   ├── generators/                              NEW
│   │   ├── pyproject.toml
│   │   ├── packetready_eval/
│   │   │   ├── __init__.py
│   │   │   ├── docs/
│   │   │   │   ├── license_pdf.py
│   │   │   │   ├── dea_pdf.py
│   │   │   │   ├── board_cert_pdf.py
│   │   │   │   └── malpractice_pdf.py
│   │   │   ├── scan_artifacts.py                rasterize + skew + JPEG
│   │   │   └── packets.py                       generate all 5 packets from one entry point
│   │   └── README.md
│   ├── runners/                                 NEW
│   │   ├── run.py                               main CLI
│   │   ├── extractor_client.py                  HTTP client for the P3 extractor endpoint
│   │   ├── metrics.py                           per-field accuracy, per-doc-type rollups
│   │   └── compare.py                           golden vs extracted comparison
│   └── results/                                 NEW
│       ├── baseline.json                        empty until P3; P3 commits first real numbers
│       └── <ISO-timestamp>.json                 one per `run.py` invocation
```

---

## Golden JSON shape

One file per packet at `<packet>/golden.json`. Field names must match the Phase 3 extractor's structured-output schema; treat this as the contract. **camelCase** throughout — matches `DomainJson.Options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase` set in P1, and ASP.NET Core's wire default. The Python runner reads camelCase keys; only generation is slightly stilted.

```json
{
  "packetId": "packet-001-clean-anderson",
  "label": "Dr. Henry Anderson",
  "documents": [
    {
      "type": "license",
      "filename": "license.pdf",
      "fields": {
        "fullName": "Henry Anderson, MD",
        "licenseNumber": "MD-NY-99001",
        "state": "NY",
        "issueDate": "2020-04-15",
        "expiryDate": "2027-04-14",
        "status": "Active"
      }
    },
    {
      "type": "dea",
      "filename": "dea.pdf",
      "fields": {
        "fullName": "Henry Anderson",
        "deaNumber": "BA1234567",
        "expiryDate": "2027-08-31",
        "status": "Active",
        "schedules": ["II", "III", "IV", "V"]
      }
    },
    {
      "type": "boardCert",
      "filename": "board-cert.pdf",
      "fields": {
        "fullName": "Henry Anderson, MD",
        "board": "ABIM",
        "specialty": "Internal Medicine",
        "issueDate": "2018-06-01",
        "expiryDate": "2028-06-01",
        "status": "Active"
      }
    },
    {
      "type": "malpractice",
      "filename": "malpractice.pdf",
      "fields": {
        "fullName": "Henry Anderson, MD",
        "carrier": "MedProtect Mutual",
        "policyNumber": "MPM-NY-00099001",
        "expiryDate": "2026-12-31",
        "status": "Active"
      }
    }
  ],
  "plantedConflicts": [],
  "_notes": "All four docs consistent; clean baseline for accuracy floor measurement."
}
```

### `fullName` per doc — design seed for P3

Every doc carries its own `fullName`. P1's `ProviderProfile.FullName` is one level up — a single string, not per-doc. The contract here says the extractor emits per-doc names (needed for the P4 `identity_coherence` validator to see "Jane Calloway" vs "Jane C. Calloway-Smith"), and P3 needs an **aggregator** layer between extraction rows and `ProviderProfile` that decides whether to: (a) keep the per-doc names alongside a reconciled `FullName`; (b) reconcile to a single canonical name + drop the rest; or (c) keep all per-doc names and surface them only through validators. Lock the choice before extractor rows hit the DB. Cheapest: (a) — keep the extracted per-doc names on the extraction rows, derive `ProviderProfile.FullName` by aggregator policy (e.g. "license takes precedence; flag a Minor if other docs disagree by ≥ Levenshtein 3"). Defer the policy to P3's first PR; bake the *option* in the extraction-row schema now.

### Two modes of the generator — clean vs conflict

The "generator writes both PDFs and `golden.json` from one source so they can't drift" claim only holds **for clean packets**. Conflict packets deliberately introduce disagreement; the generator and the golden file then carry two different concerns:

| Packet mode | What's in `golden.json` | What's in the PDFs |
|---|---|---|
| **Clean** | Ground truth (one set of field values per doc). | Render those exact values. |
| **Conflict** | Per-doc actual contents (each doc's fields reflect what its PDF prints) **plus** a `plantedConflicts` marker describing the disagreement. | Each PDF renders its own (deliberately inconsistent) values. |

The generator's `PACKET_SPECS` literal encodes both: clean packets get one shared field block, conflict packets get per-doc field overrides + a `plantedConflicts` list. Each conflict-packet PDF still has a single source of truth in the Python literal — drift between a PDF and its corresponding `documents[i].fields` block is impossible by construction. Drift between two docs is the *point*.

### `plantedConflicts` schema

Cross-document only — never PDF-vs-golden. Each entry names two or more `sources` that disagree and what kind of disagreement was planted, so the Phase 4 validator runner can score recall.

```json
"plantedConflicts": [
  {
    "kind": "name_variant",
    "sources": ["license", "malpractice"],
    "description": "license: 'Jane Calloway'; malpractice: 'Jane C. Calloway-Smith'",
    "expectedSeverity": "Critical"
  },
  {
    "kind": "expiry_mismatch",
    "field": "license.expiryDate",
    "sources": ["license", "malpractice"],
    "description": "license.pdf shows expiry 2026-09-30; malpractice.pdf's Licensee footer records expiry 2027-09-30 for the same license number",
    "expectedSeverity": "Critical"
  }
]
```

`kind` values used in P2: `name_variant`, `expiry_mismatch`. Phase 4 will add `taxonomy_specialty_mismatch`, `address_drift`, etc.

---

## Packet specs (five)

| # | Directory | Profile | Scan? | Conflict | Expected score* |
|---|---|---|---|---|---|
| 001 | `packet-001-clean-anderson` | Anderson · NY · Internal Med | no | none | 100 |
| 002 | `packet-002-clean-bautista` | Bautista · CA · Cardiology | no | none | 100 |
| 003 | `packet-003-conflict-name` | Calloway · NY · ER | no | `name_variant` cross-doc (license: "Jane Calloway"; malpractice: "Jane C. Calloway-Smith") | per-doc extraction = 100% (each PDF reads what it shows); cross-doc conflict unmeasured in P2 (no LLM validator), P4 reports as Critical |
| 004 | `packet-004-conflict-expiry` | Diallo · IL · Family Med | no | `expiry_mismatch` cross-doc (license expiry on license.pdf = 2026-09-30; license expiry as recorded on malpractice.pdf's "Licensee" footer = 2027-09-30) | per-doc extraction = 100% (each PDF reads what it shows); P4 surfaces the cross-doc conflict |
| 005 | `packet-005-scanned-anderson` | rasterized clone of 001 | yes | none | 100 once extracted; accuracy bucket scopes "scanned" |

*Note: P2 doesn't compute the score from extractions — that's the P1 scoring path, which P3 reconnects once extractor output exists. The "expected" column is informational, locked in via `golden.json` for downstream phases to compare against.*

The two variety dimensions (state + specialty) across 001/002 give the per-doc-type accuracy table somewhere to differ; without that, the extractor could overfit to a single layout and we wouldn't know.

### What "conflict" means here — two axes

There are two failure modes a packet can exercise, and they answer different questions. The P2 doc earlier conflated them; calling it out:

| Axis | Question | Where measured |
|---|---|---|
| **Extractor accuracy** | Did the extractor read what's on the PDF? | P3 forward: per-field exact match against `golden.json`'s recorded PDF contents. |
| **Cross-doc conflict** | Do multiple sources agree? | P4 forward: LLM validators reading the cross-doc story; recall against `planted_conflicts`. |

A `planted_conflict` is **always cross-document** — two PDFs deliberately disagree, both reflected accurately in `golden.json`, and the marker says "we expect a validator to surface this." There is no "PDF vs golden" axis — `golden.json` always describes what the PDFs actually contain.

---

## File-by-file

### `evals/generators/packetready_eval/docs/license_pdf.py`

```python
from dataclasses import dataclass
from pathlib import Path
from reportlab.lib.pagesizes import LETTER
from reportlab.lib.units import inch
from reportlab.pdfgen import canvas

@dataclass
class LicenseFields:
    full_name: str
    license_number: str
    state: str
    issue_date: str          # ISO YYYY-MM-DD
    expiry_date: str
    status: str

def render(fields: LicenseFields, out: Path) -> None:
    """
    Emits a single-page state medical license. Layout is deliberately
    boring — title block, two-column body, signature line. The first
    real extractor will see this same layout 5–10 times; layout drift
    waits for P4.
    """
    c = canvas.Canvas(str(out), pagesize=LETTER)
    # ...
    c.showPage()
    c.save()
```

Same shape for `dea_pdf.py`, `board_cert_pdf.py`, `malpractice_pdf.py`. Each module exposes a `render(fields, out)` callable.

**Layout discipline:** every PDF carries a header with the issuing body, a body grid (label-value pairs), and a footer with a fake authority signature. Same fonts (Helvetica + Helvetica-Bold) across modules. Variation comes from content, not chrome — extractor robustness to chrome variation lands in P4 with the scaled-up dataset.

### `evals/generators/packetready_eval/scan_artifacts.py`

```python
def rasterize_and_degrade(src_pdf: Path, dst_pdf: Path, *,
                         dpi: int = 200,
                         skew_degrees: float = 0.7,
                         jpeg_quality: int = 70) -> None:
    """
    PyMuPDF renders each page to PNG; PIL rotates by `skew_degrees` and
    re-saves as JPEG at the given quality; ReportLab repackages the JPEGs
    into a new PDF. Mimics the fax-pipeline artifacts P3's Sonnet vision
    needs to be robust against.

    Reproducibility: same source + same params produces a visually-identical
    PDF, but JPEG quantization is not guaranteed byte-identical across
    Pillow versions. Pin Pillow + PyMuPDF in pyproject.toml so checked-in
    dataset bytes only change when the pin moves; otherwise `git diff
    evals/dataset/` becomes noisy across environments.

    No random noise in P2 — that's P4 when the dataset scales.
    """
```

### `evals/generators/packetready_eval/packets.py`

The single entry point. Generates all 5 packets into `evals/dataset/` from a Python literal that mirrors `golden.json`. Source of truth for both the PDFs and the golden files — the generator writes `golden.json` itself so they can't drift.

```python
def generate_all(output_root: Path) -> None:
    """Idempotent: wipes and regenerates."""
    for spec in PACKET_SPECS:
        write_packet(output_root / spec.id, spec)
```

CLI entry: `python -m packetready_eval.packets evals/dataset/`.

### Extractor HTTP contract (locked in P2; P3 only fills in the body)

Request shape is locked here so P3 doesn't redesign the surface. Multipart form, conventional choice for PDF upload — works with curl, browsers, Python `httpx`, .NET `IFormFile` binding.

```
POST /api/extract
Content-Type: multipart/form-data
  file:    the PDF bytes        (form field name: "file")
  docType: one of license | dea | boardCert | malpractice | cv  (form field: "docType")

Response (200 OK):
  application/json
  { "fields": { ... } }   ← flat dict, keys match the doc-type's section in golden.json

Response (4xx): RFC 7807 ProblemDetails (same convention as the rest of the API)
```

### `evals/runners/extractor_client.py`

```python
import httpx

@dataclass
class ExtractorClient:
    base_url: str = "http://localhost:5099"

    def extract(self, pdf_path: Path, doc_type: str) -> dict:
        """
        POST /api/extract — multipart form. Returns the extractor's per-field
        output as a flat dict (same shape as the doc's `fields` in golden.json).
        In Phase 2 this endpoint returns {"fields": {}} for every request —
        the runner is exercising the harness, not the model.
        """
        with open(pdf_path, "rb") as f:
            resp = httpx.post(
                f"{self.base_url}/api/extract",
                files={"file": (pdf_path.name, f, "application/pdf")},
                data={"docType": doc_type},
                timeout=30.0,
            )
        resp.raise_for_status()
        return resp.json().get("fields", {})
```

### `evals/runners/compare.py`

Per-field exact match. Date normalization is `YYYY-MM-DD` only (`golden.json` is normalized; extractor output is too); no fuzzy date parsing in P2. List-valued fields like `schedules` use sorted set equality. Status enum is case-sensitive.

**Extra fields in extractor output are ignored, not penalized.** If the extractor returns `address` but golden doesn't declare it, the runner doesn't score it for or against. Golden defines the contract; new fields land in P4 once they pay rent. Missing fields (golden declares it, extractor doesn't emit it) count as a miss — fail-closed for fields we said we wanted.

```python
@dataclass
class FieldResult:
    doc_type: str
    field: str
    expected: object
    extracted: object
    match: bool
```

### `evals/runners/metrics.py`

Three rollups:
1. **Per-field accuracy** across all packets and docs: `matches / total`. Keyed `<docType>.<fieldName>`.
2. **Per-doc-type accuracy**: same numerator, scoped to one doc type.
3. **Per-packet accuracy**: a packet's `matches / total_fields`.

The Phase 4 conflict-recall metric reads `plantedConflicts` from `golden.json`; out of scope here.

### Results JSON schema

`evals/results/<ISO-timestamp>.json` and `evals/results/baseline.json` share the schema. Field names are enumerated literally so the gate can compare apples to apples — adding a new field is a schema change that requires a baseline rebuild.

```json
{
  "generatedAt": "2026-05-21T20:45:10Z",
  "stub": true,
  "perField": {
    "license.fullName": 0.0,
    "license.licenseNumber": 0.0,
    "license.state": 0.0,
    "license.issueDate": 0.0,
    "license.expiryDate": 0.0,
    "license.status": 0.0,
    "dea.fullName": 0.0,
    "dea.deaNumber": 0.0,
    "dea.expiryDate": 0.0,
    "dea.status": 0.0,
    "dea.schedules": 0.0,
    "boardCert.fullName": 0.0,
    "boardCert.board": 0.0,
    "boardCert.specialty": 0.0,
    "boardCert.issueDate": 0.0,
    "boardCert.expiryDate": 0.0,
    "boardCert.status": 0.0,
    "malpractice.fullName": 0.0,
    "malpractice.carrier": 0.0,
    "malpractice.policyNumber": 0.0,
    "malpractice.expiryDate": 0.0,
    "malpractice.status": 0.0
  },
  "perDocType": {
    "license": 0.0,
    "dea": 0.0,
    "boardCert": 0.0,
    "malpractice": 0.0
  },
  "perPacket": {
    "packet-001-clean-anderson": 0.0,
    "packet-002-clean-bautista": 0.0,
    "packet-003-conflict-name": 0.0,
    "packet-004-conflict-expiry": 0.0,
    "packet-005-scanned-anderson": 0.0
  }
}
```

`stub: true` is committed by P2 to mark the baseline as not-real. P3's first PR flips this to `false` and writes real numbers. The gate honors `stub: true` by skipping regression comparison — until P3, the gate is a syntax check, not a benchmark.

### Baseline update protocol

The simple rule: **each PR that improves accuracy commits the new `baseline.json` in the same PR.** No "update on merge" automation, no separate baseline branch.

Two PRs landing in parallel both touching `baseline.json` will conflict at merge. Resolution: re-run `python -m evals.runners.run --check-against …` after merge resolution and commit the higher numbers. Merge conflicts on a metrics file are an honest signal that two changes need to be re-measured together; the runner's reproducibility (same dataset + same prompts = same numbers) makes this cheap.

If P6 wires this into CI, the rule becomes: CI fails on > 2 pp drop relative to `baseline.json` in `main`; merging an improvement requires the PR to bump baseline. Same rule, enforced by the bot.

### `evals/runners/run.py`

CLI surface, locked:

```
python -m evals.runners.run \
  --dataset evals/dataset/ \
  --output  evals/results/ \
  --api-url http://localhost:5099   (default)
  --check-against evals/results/baseline.json   (optional; enables the regression gate)
```

```python
def main() -> int:
    args = parse_args()
    packets = load_packets(args.dataset)
    client = ExtractorClient(args.api_url)
    results = run_all(packets, client)            # nested dict: packet→doc→fields
    print_table(results)                          # to stdout
    write_results(args.output, results)
    if args.check_against:
        return regression_gate(results, args.check_against)  # exit 1 on > 2 pp drop
    return 0
```

### `apps/api/Application/Extraction/ExtractDocumentEndpoint.cs`

```csharp
// P2 stub. Returns empty fields so the runner has something to call.
// P3 replaces the BODY with the Haiku-classify → Sonnet-extract flow;
// the request/response SHAPE here is the locked contract.
app.MapPost("/api/extract", async (
    IFormFile file,
    [FromForm] string docType,
    CancellationToken ct) =>
{
    // P2: ignore inputs, return empty contract-shaped response.
    return Results.Ok(new { fields = new Dictionary<string, object>() });
})
.DisableAntiforgery();   // form POSTs from a CLI; no cookies, no XSRF surface
```

---

## Task order

1. **`evals/generators/` Python project** — `pyproject.toml`, venv, ReportLab + Pillow + PyMuPDF deps locked. `python -c "import reportlab"` works.
2. **License + DEA PDF generators** — two simplest layouts; smoke-render one of each into `/tmp` and eyeball.
3. **Board cert + malpractice PDF generators** — same pattern.
4. **Scan-artifact pipeline** — rasterize/skew/JPEG round-trip. Smoke-render packet 001's license through it; confirm the output is a valid PDF that still reads visually.
5. **`packets.py` entry point + the 5 packet specs** — generate the dataset directory.
6. **Hand-verify clean packets (001, 002)** — open every PDF and confirm what's printed matches `golden.json` line by line. A typo in `PACKET_SPECS` corrupts both the PDF and the golden file silently, accuracy stays 100% on the wrong value, and the regression gate never catches it. Cheap insurance.
7. **Hand-verify conflict packets (003, 004)** — open the disagreeing PDFs side-by-side; confirm a human can see the difference *and* that the `plantedConflicts` marker accurately names the sources and the kind. If the contrast isn't visible, regenerate with sharper field rendering.
8. **`evals/runners/` skeleton** — `compare.py`, `metrics.py`, `extractor_client.py` (with empty-response stub baked in).
9. **`run.py` end-to-end** — load packets, hit the stub, build results, print table, write JSON.
10. **`apps/api` stub endpoint** — `POST /api/extract` accepts multipart, returns `{"fields": {}}`. CORS not needed (runner doesn't run in a browser).
11. **Live run** — `python -m evals.runners.run --dataset evals/dataset/ --output evals/results/` produces an all-zeros table and a results JSON.
12. **Regression gate logic** — `--check-against` flag; compare every per-field metric to baseline; honor `stub: true` (skip comparison, just syntax-check); exit 1 on > 2 pp regression when both sides are `stub: false`. Smoke-test by writing a fake baseline with all-100% non-stub and confirming the gate fails loudly.
13. **Commit `evals/results/baseline.json`** as all-zeros + `stub: true`. P3's first PR flips `stub` to `false` and writes real numbers.
14. **Gate verification** — walk the 6 DoD checkboxes.

Order matters: 4 depends on 2; 5 depends on 2+3+4; 6+7 depend on 5; 9 depends on 8; 11 depends on 9+10; 12 depends on 11.

---

## Risks / open

- **PDF realism.** Synthetic PDFs that are too clean make P3 accuracy claims non-transferable to real customer documents. Plant deliberate noise in `packet-005-scanned`: skew, JPEG, but resist deeper artifacts (handwritten signatures, redactions, multi-page faxes) until P4. The five packets are a contract for the extractor to clear, not a stress test.
- **Golden-label correctness.** Errors in `golden.json` silently corrupt every downstream metric. Mitigation: the generator writes `golden.json` from the same Python literal it renders the PDFs from. Drift between the two is impossible by construction. Eyeball the scanned packet anyway after step 4 — rasterization could damage a date in a way that makes the PDF unreadable while `golden.json` still claims the original value.
- **Date drift.** Hard-coded dates in PDFs become stale (a license that expired in 2024 won't read as "expires soon" in 2027). P2 accepts this — the dataset is regenerated when needed, not auto-resolved at runtime. Different tradeoff from `evals/fixtures/`, where the seed CLI resolves `{today±N}` placeholders. Document the trade-off; don't bridge it.
- **Extractor stub returning `{}`.** Means the all-zeros baseline is a real baseline. When P3 first commits real numbers, the regression gate will fire on every PR after that (any change above the new baseline is fine; any drop is a real regression).
- **Pinning the harness vs. iterating.** The schema in `golden.json` becomes a contract with the P3 extractor's structured output. Resist "improving" the schema during P3 — every field rename is a dataset re-label.

- **Two-runtime dev tax.** Local dev now needs both .NET and Python installed; running the eval requires the API running in one terminal and `python -m evals.runners.run` in another. CI needs both runtimes. Defensible — ReportLab is genuinely better than the C# alternatives at PDF layout, and the cost of a second runtime is much smaller than the cost of bad fixtures — but real. Document the two-terminal flow in `evals/generators/README.md` so future-me doesn't have to re-derive it.

- **P2's 5-packet accuracy is NOT a benchmark — it's a harness sanity check.** When P3 lands and produces the first non-zero numbers on this dataset, do **not** quote them anywhere user-facing. The accuracy claim in the README, the design doc, the eventual outreach — all of those reference the **50-packet P4 set**. The 5-packet P2 set exists to detect regressions, not to ground accuracy assertions. Mixing them up is the path to "we claimed 95% but it was on 5 hand-picked packets" being a question someone asks at exactly the wrong moment.

---

## Out of scope (resist)

- Real Sonnet calls. P3 problem.
- LLM-augmented validators (`identity_coherence`, `npi_taxonomy_match`). P4 problem.
- Conflict recall / precision metrics. P4 problem.
- Score correlation (Spearman vs hand-labeled tier). P4 problem.
- 50-packet scale, NPPES distribution sampling, programmatic packet generation. P4.
- CI integration of the regression gate (Github Actions). P6 or deferred.
- A UI for browsing dataset packets. Operator-only artifact; not a user surface.
- Real OCR variation (typewriter fonts, handwritten signatures, redactions). P4.

---

## What gets written when Phase 2 closes

Append a one-line outcome note to [build-plan.md](../build-plan.md) Status. Then write `phase-3-extractors.md`. Topics: document store schema (`documents` + `document_extractions`), Haiku classifier prompt, 5 Sonnet extractor prompts (license / DEA / board cert / malpractice / CV), prompt-hash emission, vision token-cost budget, citation `field_locations` capture, the re-extraction idempotency policy.

The first thing P3 does is replace the stub at `apps/api/.../ExtractDocumentEndpoint.cs` with the real classify → extract flow — at which point the harness produces non-zero numbers for the first time and the regression gate becomes load-bearing.
