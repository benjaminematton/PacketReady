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

One file per packet at `<packet>/golden.json`. Field names must match the Phase 3 extractor's structured-output schema; treat this as the contract.

```json
{
  "packet_id": "packet-001-clean-anderson",
  "label": "Dr. Henry Anderson",
  "documents": [
    {
      "type": "license",
      "filename": "license.pdf",
      "fields": {
        "full_name": "Henry Anderson, MD",
        "license_number": "MD-NY-99001",
        "state": "NY",
        "issue_date": "2020-04-15",
        "expiry_date": "2027-04-14",
        "status": "Active"
      }
    },
    {
      "type": "dea",
      "filename": "dea.pdf",
      "fields": {
        "full_name": "Henry Anderson",
        "dea_number": "BA1234567",
        "expiry_date": "2027-08-31",
        "status": "Active",
        "schedules": ["II", "III", "IV", "V"]
      }
    },
    {
      "type": "board_cert",
      "filename": "board-cert.pdf",
      "fields": {
        "full_name": "Henry Anderson, MD",
        "board": "ABIM",
        "specialty": "Internal Medicine",
        "issue_date": "2018-06-01",
        "expiry_date": "2028-06-01",
        "status": "Active"
      }
    },
    {
      "type": "malpractice",
      "filename": "malpractice.pdf",
      "fields": {
        "full_name": "Henry Anderson, MD",
        "carrier": "MedProtect Mutual",
        "policy_number": "MPM-NY-00099001",
        "expiry_date": "2026-12-31",
        "status": "Active"
      }
    }
  ],
  "planted_conflicts": [],
  "_notes": "All four docs consistent; clean baseline for accuracy floor measurement."
}
```

For the conflict packets, `planted_conflicts` carries machine-readable markers so the Phase 4 cross-doc validator runner can score recall:

```json
"planted_conflicts": [
  {
    "kind": "name_variant",
    "sources": ["license", "malpractice"],
    "description": "license: 'Jane Calloway'; malpractice: 'Jane C. Calloway-Smith'",
    "expected_severity": "Critical"
  }
]
```

`kind` values for P2: `name_variant`, `expiry_mismatch`. Phase 4 will add `taxonomy_specialty_mismatch`, `address_drift`, etc.

---

## Packet specs (five)

| # | Directory | Profile | Scan? | Conflict | Expected score* |
|---|---|---|---|---|---|
| 001 | `packet-001-clean-anderson` | Anderson · NY · Internal Med | no | none | 100 |
| 002 | `packet-002-clean-bautista` | Bautista · CA · Cardiology | no | none | 100 |
| 003 | `packet-003-conflict-name` | Calloway · NY · ER | no | `name_variant` (license vs malpractice) | 100 cross-doc *unmeasured in P2* (no LLM validator); P4 reports it as Critical |
| 004 | `packet-004-conflict-expiry` | Diallo · IL · Family Med | no | `expiry_mismatch` (license PDF vs golden truth) | depends on which truth wins; Phase 4 surfaces the drift |
| 005 | `packet-005-scanned-anderson` | rasterized clone of 001 | yes | none | 100 once extracted; accuracy bucket scopes "scanned" |

*Note: P2 doesn't compute the score from extractions — that's the P1 scoring path, which P3 reconnects once extractor output exists. The "expected" column is informational, locked in via `golden.json` for downstream phases to compare against.*

The two variety dimensions (state + specialty) across 001/002 give the per-doc-type accuracy table somewhere to differ; without that, the extractor could overfit to a single layout and we wouldn't know.

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

    Deterministic: same source + same params = byte-identical output.
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

### `evals/runners/extractor_client.py`

```python
@dataclass
class ExtractorClient:
    base_url: str = "http://localhost:5099"

    def extract(self, pdf_path: Path, doc_type: str) -> dict:
        """
        POST /api/extract — Phase 3 endpoint. Returns the extractor's
        per-field output as a flat dict (same shape as the doc's `fields`
        in golden.json). In Phase 2 this endpoint returns {} for every
        request — the runner is exercising the harness, not the model.
        """
```

### `evals/runners/compare.py`

Per-field exact match. Date normalization is `YYYY-MM-DD` only (`golden.json` is normalized; extractor output is too); no fuzzy date parsing in P2. List-valued fields like `schedules` use sorted set equality. Status enum is case-sensitive.

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
1. **Per-field accuracy** across all packets and docs: `matches / total`.
2. **Per-doc-type accuracy**: same numerator, scoped to one doc type.
3. **Per-packet accuracy**: a packet's `matches / total_fields`.

The Phase 4 conflict-recall metric reads `planted_conflicts` from `golden.json`; out of scope here.

### `evals/runners/run.py`

```python
def main() -> int:
    args = parse_args()                          # --dataset, --output, --check-against
    packets = load_packets(args.dataset)
    client = ExtractorClient(args.api_url)
    results = run_all(packets, client)            # nested dict: packet→doc→fields
    table = print_table(results)                  # to stdout
    out_path = write_results(args.output, results)
    if args.check_against:
        return regression_gate(results, args.check_against)  # exit 1 on > 2 pp drop
    return 0
```

### `apps/api/Application/Extraction/ExtractDocumentEndpoint.cs`

```csharp
// P2 stub. Returns empty fields so the runner has something to call.
// P3 replaces this with the Haiku-classify → Sonnet-extract flow.
app.MapPost("/api/extract", async (HttpRequest req, CancellationToken ct) =>
{
    return Results.Ok(new { fields = new Dictionary<string, object>() });
});
```

---

## Task order

1. **`evals/generators/` Python project** — `pyproject.toml`, venv, ReportLab + Pillow + PyMuPDF deps locked. `python -c "import reportlab"` works.
2. **License + DEA PDF generators** — two simplest layouts; smoke-render one of each into `/tmp` and eyeball.
3. **Board cert + malpractice PDF generators** — same pattern.
4. **Scan-artifact pipeline** — rasterize/skew/JPEG round-trip. Smoke-render packet 001's license through it; confirm the output is a valid PDF that still reads visually.
5. **`packets.py` entry point + the 5 packet specs** — generate the dataset directory; eyeball one of each (clean, scanned, name-variant).
6. **Manual cross-check** — open `packet-003-conflict-name`'s license.pdf and malpractice.pdf side-by-side, confirm a human can see the name difference. If not, regenerate with sharper contrast.
7. **`evals/runners/` skeleton** — `compare.py`, `metrics.py`, `extractor_client.py` (with empty-response stub baked in).
8. **`run.py` end-to-end** — load packets, hit the stub, build results, print table, write JSON.
9. **`apps/api` stub endpoint** — `POST /api/extract` returns `{}`. CORS not needed (runner doesn't run in a browser).
10. **Live run** — `python evals/runners/run.py --dataset evals/dataset/ --output evals/results/` produces an all-zeros table and a results JSON.
11. **Regression gate logic** — `--check-against` flag; compare every per-field metric to baseline; exit 1 on > 2 pp regression. Smoke-test by writing a fake baseline with all-100% and confirming the gate fails loudly.
12. **Commit `evals/results/baseline.json`** as all-zeros. P3 will overwrite with first real numbers.
13. **Gate verification** — walk the 6 DoD checkboxes.

Order matters: 4 depends on 2; 5 depends on 2+3+4; 6 depends on 5; 8 depends on 7; 10 depends on 8+9; 11 depends on 10.

---

## Risks / open

- **PDF realism.** Synthetic PDFs that are too clean make P3 accuracy claims non-transferable to real customer documents. Plant deliberate noise in `packet-005-scanned`: skew, JPEG, but resist deeper artifacts (handwritten signatures, redactions, multi-page faxes) until P4. The five packets are a contract for the extractor to clear, not a stress test.
- **Golden-label correctness.** Errors in `golden.json` silently corrupt every downstream metric. Mitigation: the generator writes `golden.json` from the same Python literal it renders the PDFs from. Drift between the two is impossible by construction. Eyeball the scanned packet anyway after step 4 — rasterization could damage a date in a way that makes the PDF unreadable while `golden.json` still claims the original value.
- **Date drift.** Hard-coded dates in PDFs become stale (a license that expired in 2024 won't read as "expires soon" in 2027). P2 accepts this — the dataset is regenerated when needed, not auto-resolved at runtime. Different tradeoff from `evals/fixtures/`, where the seed CLI resolves `{today±N}` placeholders. Document the trade-off; don't bridge it.
- **Extractor stub returning `{}`.** Means the all-zeros baseline is a real baseline. When P3 first commits real numbers, the regression gate will fire on every PR after that (any change above the new baseline is fine; any drop is a real regression).
- **Pinning the harness vs. iterating.** The schema in `golden.json` becomes a contract with the P3 extractor's structured output. Resist "improving" the schema during P3 — every field rename is a dataset re-label.

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
