#!/usr/bin/env python3
"""
One-off generator for data/nppes-sample-2026.csv.

Run once when refreshing the snapshot:
    python3 data/build_nppes_sample.py

Output is committed; the runtime sampler
(evals/generators/packetready_eval/nppes_sampling.py) uniform-samples this CSV.

Why synthetic instead of a real NPPES dump:
  Validators only reason about (specialty, taxonomy_code) coherence and the
  state/year fields per-packet. No downstream code dereferences the NPI in any
  registry. A real NPI attached to a fabricated "Dr. Anderson" profile in a
  committed fixture would couple the repo to real-provider identifiers with
  zero validator benefit. Synthetic Luhn-valid NPIs carry the same plausibility
  to the extractor (it's reading "NPI: 1234567890" off a synthetic PDF) without
  the optics cost.

Distributions are derived from public aggregate stats — sourced once, hard-coded
as dict literals below. Refresh the numbers if the snapshot ages past ~2y.
"""

from __future__ import annotations

import argparse
import csv
import random
from pathlib import Path

# ---------------------------------------------------------------------------
# Distributions (sources cited inline; refresh if snapshot ages)
# ---------------------------------------------------------------------------

# State weights ~ US Census 2020 state resident population in millions.
# Proxy for physician density — AAMC State Physician Workforce Data diverges
# by ~10-25% on outliers (MA, NY over-represented; MS, NV under-represented),
# but for synthetic-packet realism population is close enough and verifiable
# in one click. Includes DC.
# Source: https://www.census.gov/data/tables/time-series/demo/popest/2020s-state-total.html
STATE_WEIGHTS: dict[str, float] = {
    "CA": 39.5, "TX": 29.1, "FL": 21.5, "NY": 20.2, "PA": 13.0,
    "IL": 12.8, "OH": 11.8, "GA": 10.7, "NC": 10.4, "MI": 10.1,
    "NJ":  9.3, "VA":  8.6, "WA":  7.7, "AZ":  7.2, "MA":  7.0,
    "TN":  6.9, "IN":  6.8, "MD":  6.2, "MO":  6.2, "WI":  5.9,
    "CO":  5.8, "MN":  5.7, "SC":  5.1, "AL":  5.0, "LA":  4.7,
    "KY":  4.5, "OR":  4.2, "OK":  4.0, "CT":  3.6, "UT":  3.3,
    "IA":  3.2, "NV":  3.1, "AR":  3.0, "MS":  3.0, "KS":  2.9,
    "NM":  2.1, "NE":  2.0, "ID":  1.8, "WV":  1.8, "HI":  1.5,
    "NH":  1.4, "ME":  1.4, "MT":  1.1, "RI":  1.1, "DE":  1.0,
    "SD":  0.9, "ND":  0.8, "AK":  0.7, "VT":  0.6, "WY":  0.6,
    "DC":  0.7,
}

# Specialty weights ~ AAMC 2022 Physician Specialty Data Report active-physician
# counts, in thousands, rounded. Keys are NUCC `Classification` strings (must
# match the NUCC CSV exactly — Allergy & Immunology, Obstetrics & Gynecology,
# Orthopaedic spelling, etc.). Tail folded into broader buckets.
# Source: https://www.aamc.org/data-reports/workforce/data/2022-physician-specialty-data-report
SPECIALTY_WEIGHTS: dict[str, float] = {
    "Internal Medicine":                120,
    "Family Medicine":                  110,
    "Pediatrics":                        71,
    "Psychiatry & Neurology":            50,   # combined umbrella in NUCC
    "Emergency Medicine":                50,
    "Anesthesiology":                    45,
    "Obstetrics & Gynecology":           43,
    "Surgery":                           37,   # general surgery
    "Radiology":                         30,
    "Orthopaedic Surgery":               30,
    "Ophthalmology":                     21,
    "Pathology":                         17,
    "Dermatology":                       14,
    "Otolaryngology":                    10,
    "Urology":                           10,
    "Physical Medicine & Rehabilitation": 9,
    "Plastic Surgery":                    7,
    "Neurological Surgery":               5,
    "Allergy & Immunology":               5,
    "Colon & Rectal Surgery":             2,
    "Thoracic Surgery (Cardiothoracic Vascular Surgery)": 5,
    "Nuclear Medicine":                   1,
    "Preventive Medicine":                3,
    "Medical Genetics":                   1,
}

# License issuance year distribution: triangular(low, peak, high) with peak
# 14 years before the snapshot year. Reflects median U.S. practicing-physician
# years-since-graduation of ~15y (AAMC Physician Workforce 2022, Table 1.3).
# Floor 40y to cover the long tail; ceiling 5y to keep the freshly-licensed
# bucket non-empty.
SNAPSHOT_YEAR = 2026
YEAR_RANGE_LOW = SNAPSHOT_YEAR - 40
YEAR_RANGE_HIGH = SNAPSHOT_YEAR - 5
YEAR_RANGE_PEAK = SNAPSHOT_YEAR - 14


# ---------------------------------------------------------------------------
# NPI generation
# ---------------------------------------------------------------------------

def luhn_check_digit(first_9: str) -> int:
    """
    CMS NPI check-digit spec: prepend the ISO 7812 issuer prefix "80840" to the
    first 9 NPI digits, run Luhn on the 14-digit string, return the digit that
    makes the sum divisible by 10.

    Source: CMS NPI Check Digit Specification, 2004
    (https://www.cms.gov/Regulations-and-Guidance/Administrative-Simplification/NationalProvIdentStand/Downloads/NPIcheckdigit.pdf).
    """
    total = 0
    digits = "80840" + first_9
    # Luhn doubles digits at odd positions from the right (excluding the check
    # digit position — but here we haven't appended it yet, so we double from
    # position 0 of the reversed string).
    for i, d in enumerate(reversed(digits)):
        n = int(d)
        if i % 2 == 0:
            n *= 2
            if n > 9:
                n -= 9
        total += n
    return (10 - (total % 10)) % 10


def make_synthetic_npi(rng: random.Random) -> str:
    """
    Returns a Luhn-valid 10-digit NPI with the Type 1 (individual provider)
    prefix. NPPES allocates Type 1 starting at "1" and Type 2 (organizations)
    starting at "2"; every row in this sample carries `primary_specialty` +
    `license_state`, which are Type 1 attributes, so the prefix is fixed at 1.
    These are NOT registered NPIs — synthetic for fixtures only.
    """
    first_9 = "1" + "".join(rng.choices("0123456789", k=8))
    return first_9 + str(luhn_check_digit(first_9))


# ---------------------------------------------------------------------------
# NUCC lookup
# ---------------------------------------------------------------------------

def load_classification_to_code(nucc_csv: Path) -> dict[str, str]:
    """
    Returns {Classification: taxonomy_code} for rows under the
    "Allopathic & Osteopathic Physicians" grouping. Prefers the base (no
    Specialization) row; for classifications that have no base row (e.g.
    Radiology, Pathology, Psychiatry & Neurology), falls back to the
    lexicographically smallest subspecialization code so the mapping is stable
    across NUCC CSV row reorderings. `npi_taxonomy_match` only checks that the
    taxonomy code rolls up to the right Classification, so the specific subspec
    choice doesn't matter — determinism does.
    """
    base: dict[str, str] = {}
    subspec_codes: dict[str, list[str]] = {}
    with nucc_csv.open() as f:
        for r in csv.DictReader(f):
            if r["Grouping"] != "Allopathic & Osteopathic Physicians":
                continue
            cls = r["Classification"].strip()
            code = r["Code"].strip()
            if not cls or not code:
                continue
            if not r["Specialization"].strip():
                base[cls] = code
            else:
                subspec_codes.setdefault(cls, []).append(code)
    fallback = {cls: min(codes) for cls, codes in subspec_codes.items()}
    return {**fallback, **base}  # base wins where it exists


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    here = Path(__file__).parent
    parser.add_argument("--nucc", type=Path, default=here / "nucc-taxonomy-25.1.csv")
    parser.add_argument("--out", type=Path, default=here / "nppes-sample-2026.csv")
    parser.add_argument("-n", "--count", type=int, default=10_000)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    rng = random.Random(args.seed)
    cls_to_code = load_classification_to_code(args.nucc)

    # Fail loud if any specialty weight names a NUCC classification we can't
    # resolve — drift between this dict and the NUCC snapshot is a silent
    # taxonomy-code-of-empty-string bug otherwise.
    missing = sorted(set(SPECIALTY_WEIGHTS) - set(cls_to_code))
    if missing:
        raise SystemExit(
            "Specialty weights reference NUCC classifications absent from "
            f"{args.nucc.name}: {missing}"
        )

    states = list(STATE_WEIGHTS.keys())
    state_weights = list(STATE_WEIGHTS.values())
    specialties = list(SPECIALTY_WEIGHTS.keys())
    specialty_weights = list(SPECIALTY_WEIGHTS.values())

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=[
            "npi", "primary_specialty", "taxonomy_code",
            "license_state", "license_issuance_year",
        ])
        w.writeheader()
        for _ in range(args.count):
            specialty = rng.choices(specialties, weights=specialty_weights, k=1)[0]
            state = rng.choices(states, weights=state_weights, k=1)[0]
            year = int(rng.triangular(YEAR_RANGE_LOW, YEAR_RANGE_HIGH, YEAR_RANGE_PEAK))
            w.writerow({
                "npi": make_synthetic_npi(rng),
                "primary_specialty": specialty,
                "taxonomy_code": cls_to_code[specialty],
                "license_state": state,
                "license_issuance_year": year,
            })

    print(f"Wrote {args.count} rows to {args.out} (seed={args.seed})")


if __name__ == "__main__":
    main()
