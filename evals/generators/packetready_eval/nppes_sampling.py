"""
Runtime sampler for `data/nppes-sample-2026.csv`.

P4 task 5+ (packet generator) calls `sample_n(seed, n)` to draw n provider
profiles from the synthetic NPPES snapshot. The same (seed, n, file-contents)
triple returns byte-identical results forever — pinned by:
  - the seeded `random.Random` instance,
  - `random.Random.sample` (in-place, no replacement, order-preserving),
  - the CSV's row order being stable (the one-off `data/build_nppes_sample.py`
    writes rows in the order it draws them, which is itself seeded).

The companion `faker_for(seed)` returns a seeded `Faker` instance for the bits
NPPES doesn't carry — provider full name and address. The packet generator
calls both with the same seed so the full PACKET_SPECS list is reproducible.

Both functions are pure. No global state, no caching layer — same inputs,
same outputs, always.
"""

from __future__ import annotations

import csv
import hashlib
import random
from dataclasses import dataclass
from pathlib import Path

from faker import Faker

# Package lives at evals/generators/packetready_eval/; the snapshot lives at
# <repo>/data/nppes-sample-2026.csv → up four levels from this file.
_REPO_ROOT = Path(__file__).resolve().parents[3]
DEFAULT_NPPES_CSV = _REPO_ROOT / "data" / "nppes-sample-2026.csv"


@dataclass(frozen=True)
class SampledProfile:
    """
    One row out of the NPPES snapshot. Faker-derived fields (full name,
    address, phone) are NOT here — the caller threads them in via
    `faker_for(seed)` after sampling.
    """
    npi: str
    primary_specialty: str
    taxonomy_code: str
    license_state: str
    license_issuance_year: int


def csv_file_hash(path: Path) -> str:
    """
    First 12 chars of the CSV's sha256. Diagnostic-grade — the runner can log
    "sampled against nppes-sample csv=<hash>" so a "why did baseline.json
    change?" investigation can rule out a silently-edited CSV in one grep.
    """
    return hashlib.sha256(path.read_bytes()).hexdigest()[:12]


def sample_n(
    seed: int,
    n: int,
    *,
    nppes_csv: Path = DEFAULT_NPPES_CSV,
) -> list[SampledProfile]:
    """
    Returns n profiles drawn without replacement from the NPPES snapshot,
    in the order `random.Random(seed).sample` produced them.

    Raises ValueError if n exceeds the snapshot row count (10k as of 2026-05-25).
    """
    rows = _load_rows(nppes_csv)
    if n > len(rows):
        raise ValueError(
            f"sample_n: requested {n} but {nppes_csv.name} carries only {len(rows)} rows"
        )
    return random.Random(seed).sample(rows, n)


def faker_for(seed: int) -> Faker:
    """
    Returns a US-locale Faker seeded so every method call is deterministic
    for that seed. We seed BOTH the module-level RNG (`Faker.seed`) and the
    instance RNG (`fk.seed_instance`) — some providers reach for one, some
    the other, and skipping either leaves a residual non-determinism that
    only shows up after a Faker version bump.
    """
    fk = Faker("en_US")
    Faker.seed(seed)
    fk.seed_instance(seed)
    return fk


def _load_rows(path: Path) -> list[SampledProfile]:
    with path.open() as f:
        return [
            SampledProfile(
                npi=r["npi"],
                primary_specialty=r["primary_specialty"],
                taxonomy_code=r["taxonomy_code"],
                license_state=r["license_state"],
                license_issuance_year=int(r["license_issuance_year"]),
            )
            for r in csv.DictReader(f)
        ]


if __name__ == "__main__":
    # Smoke per P4 task 4: print 5 profiles + a state/specialty tally so the
    # human eyeballing the distribution can confirm "looks right".
    import argparse
    from collections import Counter

    parser = argparse.ArgumentParser(description="Smoke-print NPPES sample draws.")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--n", type=int, default=5)
    args = parser.parse_args()

    sample = sample_n(args.seed, args.n)
    print(f"# nppes-csv hash: {csv_file_hash(DEFAULT_NPPES_CSV)}")
    print(f"# seed={args.seed}  n={args.n}\n")
    for p in sample:
        print(f"  {p.npi}  {p.license_state}  {p.license_issuance_year}  {p.primary_specialty}")

    # Tally on a larger draw so the distribution is legible.
    big = sample_n(args.seed, 1000)
    print("\n# Distribution check (n=1000, same seed):")
    print(f"  top 5 states:      {Counter(p.license_state for p in big).most_common(5)}")
    print(f"  top 5 specialties: {Counter(p.primary_specialty for p in big).most_common(5)}")
