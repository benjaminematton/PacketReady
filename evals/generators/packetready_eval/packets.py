"""Generate all PacketReady eval packets from a single Python literal.

The literal is the source of truth: it drives the PDFs AND the golden.json.
Drift between a PDF and its corresponding `documents[i].fields` block is
impossible by construction. Drift between two docs in a conflict packet is
the point — see `planted_conflicts`.

CLI: `python -m packetready_eval.packets evals/dataset/`
"""

from __future__ import annotations

import argparse
import json
import shutil
import string
import sys
import tempfile
from dataclasses import dataclass, field, replace
from datetime import date, timedelta
from pathlib import Path
from random import Random
from typing import Any, Literal

from faker import Faker

from .conflict_planters import plant_name_variant, plant_taxonomy_specialty_mismatch
from .name_variants import CleanNamePattern, ConflictShape, names_for_clean_pattern
from .docs.board_cert_pdf import BoardCertFields, render as render_board_cert
from .docs.dea_pdf import DeaFields, render as render_dea
from .docs.license_pdf import LicenseFields, render as render_license
from .docs.malpractice_pdf import MalpracticeFields, render as render_malpractice
from .nppes_sampling import SampledProfile, faker_for, sample_n
from .scan_artifacts import rasterize_and_degrade
from .schema import BOARD_CERT, DEA, DOC_FILENAMES, LICENSE, MALPRACTICE
from .specialty_catalog import board_for_specialty


@dataclass(frozen=True)
class IdentityFields:
    """
    Provider identity (P4 task 1, slice 2). These are NOT extracted from any
    P4 document type — they live on the Provider row at create time and get
    overlaid by the aggregator at score time. The CV extractor (not shipped)
    will close this seam in a later phase; until then, the eval orchestrator
    has to pass them explicitly to ``POST /api/providers``.

    All four fields are required when emitted to golden.json. The
    orchestrator's wire validator (``ProviderIdentityValidator`` on the C#
    side) re-validates: NPI is CMS-Luhn-valid (10 digits, "80840" prefix +
    mod-10), state matches ``^[A-Z]{2}$``, DOB is in
    [1900-01-02, today], fullName non-empty.
    """
    full_name: str
    npi: str                     # 10 digits, CMS-Luhn-valid
    date_of_birth: str           # YYYY-MM-DD
    credentialing_state: str     # 2 uppercase letters


@dataclass(frozen=True)
class PacketSpec:
    id: str
    label: str
    license_fields: LicenseFields
    dea_fields: DeaFields
    board_cert_fields: BoardCertFields
    malpractice_fields: MalpracticeFields
    # Provider-identity block (slice 2). Required for the orchestrator's
    # POST /api/providers call — without it the scored profile carries
    # placeholder NPI/DOB/state and contaminates downstream metrics.
    identity_fields: IdentityFields | None = None
    # Tuple (not list) so the frozen contract extends to the conflict markers
    # themselves — `replace(spec, planted_conflicts=(*spec.planted_conflicts, m))`
    # is the only mutation path. dict elements stay mutable for forward-compat;
    # callers MUST NOT in-place mutate them.
    planted_conflicts: tuple[dict[str, Any], ...] = field(default_factory=tuple)
    notes: str = ""
    scanned: bool = False        # P005 only — rasterize+degrade after rendering
    # Which CleanNamePattern this packet's per-doc names render. Set by the
    # programmatic builder; P2 hand-crafted packets leave it None (they're
    # canonical baselines, not pattern coverage). Persisted into golden.json
    # under metadata.cleanPattern so the C# CLI can break FPs down per-pattern.
    clean_pattern: CleanNamePattern | None = None


# --- NPI Luhn helpers ---------------------------------------------------------


def npi_check_digit(base9: str) -> str:
    """Compute the CMS NPI check digit (10th position) for a 9-digit base.

    The 9-digit base is prefixed with the ISO 7812 issuer identifier "80840";
    standard Luhn-mod-10 over the 14-digit string yields the check digit.
    The 10-digit NPI = base9 + check_digit then satisfies the full Luhn
    validator on "80840"+npi.
    """
    if len(base9) != 9 or not base9.isdigit():
        raise ValueError(f"npi_check_digit: base9 must be 9 digits, got {base9!r}")
    prefixed = "80840" + base9
    total = 0
    for i, c in enumerate(reversed(prefixed)):
        d = int(c)
        if i % 2 == 0:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return str((10 - total % 10) % 10)


def npi_from_base(base9: str) -> str:
    """Build a CMS-Luhn-valid 10-digit NPI from a 9-digit base."""
    return base9 + npi_check_digit(base9)


# --- Anderson canonical fields, shared by packets 001 and 005 ----------------
# 005 is "001 with scanned chrome." Tuning a field on either packet must move
# both in lockstep — that's an invariant of the eval design, so it's expressed
# in code rather than maintained by hand.

_ANDERSON_LICENSE = LicenseFields(
    full_name="Henry Anderson, MD",
    license_number="MD-NY-99001",
    state="NY",
    issue_date="2020-04-15",
    expiry_date="2027-04-14",
    status="Active",
    taxonomy_code="207R00000X",      # Internal Medicine (matches board cert)
)
_ANDERSON_DEA = DeaFields(
    full_name="Henry Anderson",
    dea_number="BA1234567",
    expiry_date="2027-08-31",
    status="Active",
    schedules=("II", "III", "IV", "V"),
)
_ANDERSON_BOARD = BoardCertFields(
    full_name="Henry Anderson, MD",
    board="ABIM",
    specialty="Internal Medicine",
    issue_date="2018-06-01",
    expiry_date="2028-06-01",
    status="Active",
)
_ANDERSON_MALPRACTICE = MalpracticeFields(
    full_name="Henry Anderson, MD",
    carrier="MedProtect Mutual",
    policy_number="MPM-NY-00099001",
    expiry_date="2026-12-31",
    status="Active",
    licensee_license_number="MD-NY-99001",
    licensee_license_expiry="2027-04-14",
)
# Identity for packets 001 + 005 (005 is the rasterized clone of 001).
# DOB and credentialing state are not on the license; held here as the
# canonical Anderson identity so the orchestrator can pass them to
# POST /api/providers. NPI is Luhn-valid by construction via
# npi_from_base; the base9 prefix encodes "NY99001" loosely so the
# fixture stays recognizable in logs.
_ANDERSON_IDENTITY = IdentityFields(
    full_name="Henry Anderson, MD",
    npi=npi_from_base("199900001"),       # → 1999000015 (Luhn-self-verified)
    date_of_birth="1975-03-14",
    credentialing_state="NY",
)


PACKET_SPECS: list[PacketSpec] = [
    # 001 — clean, Anderson, NY, Internal Medicine.
    PacketSpec(
        id="packet-001-clean-anderson",
        label="Dr. Henry Anderson",
        license_fields=_ANDERSON_LICENSE,
        dea_fields=_ANDERSON_DEA,
        board_cert_fields=_ANDERSON_BOARD,
        malpractice_fields=_ANDERSON_MALPRACTICE,
        identity_fields=_ANDERSON_IDENTITY,
        notes="All four docs consistent; clean baseline for accuracy floor measurement.",
    ),

    # 002 — clean, Bautista, CA, Cardiology. Variety axis for the extractor.
    PacketSpec(
        id="packet-002-clean-bautista",
        label="Dr. Marisol Bautista",
        license_fields=LicenseFields(
            full_name="Marisol Bautista, MD",
            license_number="A-CA-72145",
            state="CA",
            issue_date="2016-09-12",
            expiry_date="2027-09-11",
            status="Active",
            # Base Internal Medicine code; board cert specialty "Cardiovascular
            # Disease" is a subspec that rolls up to IM via NUCC — the validator
            # is expected to accept this synonymy.
            taxonomy_code="207R00000X",
        ),
        dea_fields=DeaFields(
            full_name="Marisol Bautista",
            dea_number="FB7654321",
            expiry_date="2028-02-28",
            status="Active",
            schedules=("II", "III", "IV", "V"),
        ),
        board_cert_fields=BoardCertFields(
            full_name="Marisol Bautista, MD",
            board="ABIM",
            specialty="Cardiovascular Disease",
            issue_date="2019-10-15",
            expiry_date="2029-10-15",
            status="Active",
        ),
        malpractice_fields=MalpracticeFields(
            full_name="Marisol Bautista, MD",
            carrier="Pacific Indemnity Group",
            policy_number="PIG-CA-721450",
            expiry_date="2027-01-31",
            status="Active",
            licensee_license_number="A-CA-72145",
            licensee_license_expiry="2027-09-11",
        ),
        identity_fields=IdentityFields(
            full_name="Marisol Bautista, MD",
            npi=npi_from_base("172145001"),
            date_of_birth="1981-08-22",
            credentialing_state="CA",
        ),
        notes="All four docs consistent; second clean packet adds state+specialty variety.",
    ),

    # 003 — name_variant conflict between license and malpractice.
    PacketSpec(
        id="packet-003-conflict-name",
        label="Dr. Jane Calloway",
        license_fields=LicenseFields(
            full_name="Jane Calloway, MD",
            license_number="MD-NY-44210",
            state="NY",
            issue_date="2017-07-01",
            expiry_date="2027-06-30",
            status="Active",
            taxonomy_code="207P00000X",  # Emergency Medicine (matches board cert)
        ),
        dea_fields=DeaFields(
            full_name="Jane Calloway",
            dea_number="MC4422100",
            expiry_date="2027-12-31",
            status="Active",
            schedules=("II", "III", "IV", "V"),
        ),
        board_cert_fields=BoardCertFields(
            full_name="Jane Calloway, MD",
            board="ABEM",
            specialty="Emergency Medicine",
            issue_date="2017-11-01",
            expiry_date="2027-11-01",
            status="Active",
        ),
        # Malpractice carries the married/hyphenated surname.
        malpractice_fields=MalpracticeFields(
            full_name="Jane C. Calloway-Smith, MD",
            carrier="Atlantic Liability Mutual",
            policy_number="ALM-NY-44210-S",
            expiry_date="2026-11-30",
            status="Active",
            licensee_license_number="MD-NY-44210",
            licensee_license_expiry="2027-06-30",
        ),
        planted_conflicts=(
            {
                "kind": "name_variant",
                "shape": "HYPHENATED_SUFFIX",  # hand-coded predecessor of the planter's HYPHENATED_SUFFIX
                "field": "malpractice.fullName",
                "sources": ["license", "malpractice"],
                "description": "license: 'Jane Calloway, MD'; malpractice: 'Jane C. Calloway-Smith, MD'",
                "expectedSeverity": "Critical",
                "expected_to_flag": True,
            },
        ),
        identity_fields=IdentityFields(
            full_name="Jane Calloway, MD",   # license-side baseline; the variant is on malpractice
            npi=npi_from_base("144210001"),
            date_of_birth="1983-11-09",
            credentialing_state="NY",
        ),
        notes="Per-doc extraction must read each PDF's literal name. Cross-doc validator (P4) surfaces the variant.",
    ),

    # 004 — expiry_mismatch between license.pdf and malpractice.pdf's Licensee footer.
    PacketSpec(
        id="packet-004-conflict-expiry",
        label="Dr. Amadou Diallo",
        license_fields=LicenseFields(
            full_name="Amadou Diallo, MD",
            license_number="036-IL-58031",
            state="IL",
            issue_date="2015-03-20",
            expiry_date="2026-09-30",            # License says 2026-09-30
            status="Active",
            taxonomy_code="207Q00000X",  # Family Medicine (matches board cert)
        ),
        dea_fields=DeaFields(
            full_name="Amadou Diallo",
            dea_number="AD5803100",
            expiry_date="2027-05-31",
            status="Active",
            schedules=("II", "III", "IV", "V"),
        ),
        board_cert_fields=BoardCertFields(
            full_name="Amadou Diallo, MD",
            board="ABFM",
            specialty="Family Medicine",
            issue_date="2016-08-01",
            expiry_date="2026-08-01",
            status="Active",
        ),
        malpractice_fields=MalpracticeFields(
            full_name="Amadou Diallo, MD",
            carrier="Midwest Health Indemnity",
            policy_number="MHI-IL-580310",
            expiry_date="2027-03-31",
            status="Active",
            licensee_license_number="036-IL-58031",
            licensee_license_expiry="2027-09-30",  # Malpractice footer disagrees
        ),
        planted_conflicts=(
            {
                "kind": "expiry_mismatch",
                "field": "license.expiryDate",
                "sources": ["license", "malpractice"],
                "description": "license.pdf shows expiry 2026-09-30; malpractice.pdf's Licensee footer records expiry 2027-09-30 for the same license number 036-IL-58031",
                "expectedSeverity": "Critical",
                # P4.5 grandfather — the validator that would catch this ships
                # in 4.5. Not measured by P4's conflict_metrics; flag is here
                # for marker-schema uniformity.
                "expected_to_flag": False,
            },
        ),
        identity_fields=IdentityFields(
            full_name="Amadou Diallo, MD",
            npi=npi_from_base("158031001"),
            date_of_birth="1978-06-18",
            credentialing_state="IL",
        ),
        notes="Per-doc extraction reads each PDF accurately. P4 cross-doc validator catches the disagreement.",
    ),

    # 005 — rasterized clone of 001. Same field values; PDFs are scanned-style.
    PacketSpec(
        id="packet-005-scanned-anderson",
        label="Dr. Henry Anderson",
        license_fields=_ANDERSON_LICENSE,
        dea_fields=_ANDERSON_DEA,
        board_cert_fields=_ANDERSON_BOARD,
        malpractice_fields=_ANDERSON_MALPRACTICE,
        identity_fields=_ANDERSON_IDENTITY,   # rasterized clone shares Anderson's identity
        scanned=True,
        notes="Rasterized clone of 001 at 200dpi + 0.7° skew + JPEG q=70. Same expected fields; isolates extractor robustness to fax-pipeline artifacts.",
    ),
]


# --- golden.json serialization -------------------------------------------------

def _license_json(f: LicenseFields) -> dict[str, str]:
    return {
        "fullName": f.full_name,
        "licenseNumber": f.license_number,
        "state": f.state,
        "issueDate": f.issue_date,
        "expiryDate": f.expiry_date,
        "status": f.status,
        "taxonomyCode": f.taxonomy_code,
    }


def _dea_json(f: DeaFields) -> dict[str, Any]:
    return {
        "fullName": f.full_name,
        "deaNumber": f.dea_number,
        "expiryDate": f.expiry_date,
        "status": f.status,
        "schedules": list(f.schedules),
    }


def _board_cert_json(f: BoardCertFields) -> dict[str, str]:
    return {
        "fullName": f.full_name,
        "board": f.board,
        "specialty": f.specialty,
        "issueDate": f.issue_date,
        "expiryDate": f.expiry_date,
        "status": f.status,
    }


def _malpractice_json(f: MalpracticeFields) -> dict[str, str]:
    # Licensee-footer fields are NOT part of the scored extraction contract.
    # They live on the PDF for cross-doc conflict planting only.
    return {
        "fullName": f.full_name,
        "carrier": f.carrier,
        "policyNumber": f.policy_number,
        "expiryDate": f.expiry_date,
        "status": f.status,
    }


def _identity_json(f: IdentityFields) -> dict[str, str]:
    """Wire-format projection for the orchestrator's POST /api/providers
    `identity` block. Field names match the C# side's
    `ProviderIdentityDto`."""
    return {
        "fullName": f.full_name,
        "npi": f.npi,
        "dateOfBirth": f.date_of_birth,
        "credentialingState": f.credentialing_state,
    }


# Bumped to 2 by P4 slice 2 — adds top-level `identity` block. Readers
# (the orchestrator's score_metrics.load_planted_conflicts and friends)
# can branch on this if they need to support older goldens; today the
# regen happens in lockstep with the bump, so v1 goldens are not in
# the dataset.
GOLDEN_SCHEMA_VERSION: int = 2


def golden_for(spec: PacketSpec) -> dict[str, Any]:
    """Build the golden.json dict for a single packet spec.

    Exposed (not underscored) because the schema-invariant test loads it
    directly without invoking the PDF renderers.
    """
    metadata: dict[str, Any] = {}
    if spec.clean_pattern is not None:
        metadata["cleanPattern"] = spec.clean_pattern.name

    if spec.identity_fields is None:
        raise ValueError(
            f"PacketSpec {spec.id!r} is missing identity_fields. Slice 2 made "
            f"this required so the orchestrator can pass it to POST /api/providers; "
            f"a spec without identity_fields would force a placeholder NPI/DOB/state "
            f"and contaminate downstream score metrics."
        )

    out: dict[str, Any] = {
        "goldenSchemaVersion": GOLDEN_SCHEMA_VERSION,
        "packetId": spec.id,
        "label": spec.label,
        # Provider-identity block consumed by the orchestrator at create
        # time. Not part of the per-doc scored fields; lives as its own
        # top-level block so a reader can branch on its presence without
        # walking documents[].
        "identity": _identity_json(spec.identity_fields),
        "documents": [
            {"type": LICENSE,     "filename": DOC_FILENAMES[LICENSE],     "fields": _license_json(spec.license_fields)},
            {"type": DEA,         "filename": DOC_FILENAMES[DEA],         "fields": _dea_json(spec.dea_fields)},
            {"type": BOARD_CERT,  "filename": DOC_FILENAMES[BOARD_CERT],  "fields": _board_cert_json(spec.board_cert_fields)},
            {"type": MALPRACTICE, "filename": DOC_FILENAMES[MALPRACTICE], "fields": _malpractice_json(spec.malpractice_fields)},
        ],
        "plantedConflicts": list(spec.planted_conflicts),
        "notes": spec.notes,
    }
    if metadata:
        out["metadata"] = metadata
    return out


# --- packet write loop --------------------------------------------------------

def _render_clean(spec: PacketSpec, into_dir: Path) -> None:
    render_license(spec.license_fields, into_dir / DOC_FILENAMES[LICENSE])
    render_dea(spec.dea_fields, into_dir / DOC_FILENAMES[DEA])
    render_board_cert(spec.board_cert_fields, into_dir / DOC_FILENAMES[BOARD_CERT])
    render_malpractice(spec.malpractice_fields, into_dir / DOC_FILENAMES[MALPRACTICE])


def _write_packet(packet_dir: Path, spec: PacketSpec) -> None:
    packet_dir.mkdir(parents=True, exist_ok=True)

    if spec.scanned:
        # Render to a sibling temp dir (NOT inside packet_dir, so the
        # rasterize loop never has to discriminate input vs output paths).
        with tempfile.TemporaryDirectory(prefix="packetready-clean-") as staging_str:
            staging = Path(staging_str)
            _render_clean(spec, staging)
            for filename in DOC_FILENAMES.values():
                rasterize_and_degrade(staging / filename, packet_dir / filename)
    else:
        _render_clean(spec, packet_dir)

    golden_path = packet_dir / "golden.json"
    golden_path.write_text(
        json.dumps(golden_for(spec), indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def all_specs() -> list[PacketSpec]:
    """The full 50-packet manifest: 5 hand-crafted P2 specs + 45 programmatic P4 specs."""
    return [*PACKET_SPECS, *build_new_packets()]


def generate_all(output_root: Path) -> None:
    """Idempotent: wipes each packet directory before regenerating.

    Per-packet progress is printed because scanned packets take multiple
    seconds (200 dpi rasterization × 4 docs) and silent output reads as a
    hang in CI logs.
    """
    output_root.mkdir(parents=True, exist_ok=True)
    specs = all_specs()
    total = len(specs)
    for idx, spec in enumerate(specs, start=1):
        suffix = " (scanned)" if spec.scanned else ""
        print(f"  [{idx}/{total}] {spec.id}{suffix}", flush=True)
        packet_dir = output_root / spec.id
        if packet_dir.exists():
            shutil.rmtree(packet_dir)
        _write_packet(packet_dir, spec)


# --- P4 task 6: programmatic 45-packet builder --------------------------------

# Single anchor for every date in every new packet. Today's calendar date drifts
# (today's `now` in 6 months would shift every expiry), which would silently
# mutate goldens and break the regression gate. Pin once.
_NEW_PACKET_ANCHOR: date = date(2026, 5, 25)

# Master seed for the programmatic builder. Threading one seed through the
# sampler, faker, and bucket-shuffling RNG keeps the 45 specs byte-reproducible.
_NEW_PACKET_SEED: int = 4242

# Per-packet RNG seed = master * stride + packet_idx. 100_000 leaves five
# decimal digits of headroom for the packet index, so collisions with adjacent
# master seeds (which we don't use today, but might) only happen if the dataset
# ever exceeds 100k packets — well past the foreseeable design.
_PACKET_RNG_STRIDE: int = 100_000


_ConflictKind = Literal["none", "name", "taxonomy"]


@dataclass(frozen=True)
class _Bucket:
    """One row of the bucket plan — exhaustive over the (scanned × conflict) grid."""
    slug: str
    count: int
    scanned: bool
    conflict: _ConflictKind


# Bucket sizes for the 45 NEW packets. The 5 P2 packets already cover
# (2 clean+valid, 2 clean+conflicts, 1 scanned+clean, 0 scanned+conflicts);
# adding these gets each bucket to the DoD targets (15/15/15/5 = 50 total).
# Within conflict buckets, splits favor the lower-cost-to-tune validator
# (identity_coherence → name_variant) by one.
_BUCKET_PLAN: tuple[_Bucket, ...] = (
    _Bucket("clean",                    13, scanned=False, conflict="none"),
    _Bucket("clean-conflict-name",       7, scanned=False, conflict="name"),
    _Bucket("clean-conflict-taxonomy",   6, scanned=False, conflict="taxonomy"),
    _Bucket("scanned",                  14, scanned=True,  conflict="none"),
    _Bucket("scanned-conflict-name",     3, scanned=True,  conflict="name"),
    _Bucket("scanned-conflict-taxonomy", 2, scanned=True,  conflict="taxonomy"),
)

_TOTAL_NEW_PACKETS = sum(b.count for b in _BUCKET_PLAN)
if _TOTAL_NEW_PACKETS != 45:
    raise AssertionError(
        f"_BUCKET_PLAN must sum to 45 (got {_TOTAL_NEW_PACKETS}) — see docs/p4-dod.md"
    )


# Carrier name pool for malpractice — varied enough that the extractor sees
# multi-word, hyphenated, and acronym carriers in the same set.
_CARRIERS: tuple[str, ...] = (
    "MedProtect Mutual", "Pacific Indemnity Group", "Atlantic Liability Mutual",
    "Midwest Health Indemnity", "ProAssurance Casualty Co.", "MAG Mutual",
    "The Doctors Company", "MedMal Specialty Insurance", "Coverys Specialty",
    "Continental Casualty Co.", "ISMIE Mutual", "NORCAL Mutual",
)

# DEA registration numbers are <2 letters><7 digits>. Generated 2-letter prefix
# kept upper-case ASCII per DEA spec — DEA's own check-digit math we don't
# reproduce (the validators don't enforce it; extractor just reads the string).
_DEA_PREFIX_LETTERS = string.ascii_uppercase


def _date_str(d: date) -> str:
    return d.isoformat()


def _draw_person(faker: Faker) -> tuple[str, str]:
    """Returns (first, last) from a Faker instance."""
    first: str = faker.first_name()
    last: str = faker.last_name()
    return first, last


def _carrier_policy_prefix(carrier: str) -> str:
    """Three-letter uppercased prefix of the carrier's first word.

    Falls back to `"MED"` if the carrier name is unusually short — keeps the
    policy number well-formed without crashing for an exotic future entry."""
    head = carrier.split()[0] if carrier.split() else ""
    return (head[:3] or "MED").upper()


def _spec_from_profile(idx: int, profile: SampledProfile, faker: Faker, rng: Random,
                       *, tag: str, clean_pattern: CleanNamePattern) -> PacketSpec:
    """
    Build a clean PacketSpec from one sampled NPPES row + a Faker instance.
    All four docs end up Active and not-expired against `_NEW_PACKET_ANCHOR`,
    so the spec lands in the "clean" buckets by default; the caller layers
    planters / `scanned=True` on top for the conflict / scanned buckets.

    `clean_pattern` dictates the per-doc fullName shape (credential suffix,
    middle initial, hyphenation, whitespace). Rotated by packet_idx in
    :pyfunc:`build_new_packets` so every pattern appears in the 50-packet set.

    Identity fields:
      - NPI is `profile.npi` (NPPES snapshot is Luhn-valid by construction).
      - credentialing_state is `profile.license_state` — for clean packets
        the credentialing state matches the license state; conflict buckets
        that move one without the other will need a per-bucket override.
      - DOB is drawn from the per-packet `rng` (not faker) — faker draws
        from the same module RNG as `first_name`/`last_name`, so pulling
        a DOB out of faker shifts every subsequent packet's name. The
        per-packet `rng` is already independent of faker, so this stays
        out of the name-stability invariant.
    """
    first, last = _draw_person(faker)
    names = names_for_clean_pattern(clean_pattern, first, last)
    last_slug = "".join(c.lower() for c in last if c.isalpha()) or "anon"
    packet_id = f"packet-{idx:03d}-{tag}-{last_slug}"

    # Dates anchored to a fixed reference so goldens don't drift with today's
    # clock. rng controls the per-packet offset so each packet's dates differ.
    license_issue   = _NEW_PACKET_ANCHOR - timedelta(days=365 * rng.randint(2, 5))
    license_expiry  = license_issue + timedelta(days=365 * 6)
    dea_issue       = _NEW_PACKET_ANCHOR - timedelta(days=365 * rng.randint(1, 3))
    dea_expiry      = dea_issue + timedelta(days=365 * 3)
    board_issue     = _NEW_PACKET_ANCHOR - timedelta(days=365 * rng.randint(2, 6))
    board_expiry    = board_issue + timedelta(days=365 * 10)
    malpractice_exp = _NEW_PACKET_ANCHOR + timedelta(days=rng.randint(200, 700))

    license_number = f"MD-{profile.license_state}-{profile.npi[-5:]}"
    dea_number     = (rng.choice(_DEA_PREFIX_LETTERS)
                      + rng.choice(_DEA_PREFIX_LETTERS)
                      + profile.npi[-7:])
    # Carrier and policy-number prefix come from the SAME draw — otherwise the
    # PDF reads "Carrier: MedProtect Mutual / Policy: ALM-…" and produces a
    # silent, unplanted realism conflict for every programmatic packet.
    carrier        = rng.choice(_CARRIERS)
    policy_number  = f"{_carrier_policy_prefix(carrier)}-{profile.license_state}-{profile.npi[-6:]}"
    board          = board_for_specialty(profile.primary_specialty)
    # DOB drawn LAST so existing rng-driven values (license/dea/board/
    # malpractice dates, dea prefix letters, carrier choice) stay byte-stable
    # against pre-slice-2 generations. Plausibly 30-70 years old against
    # the anchor date — credentialing providers are post-residency at
    # minimum and rarely working past 70. Hand-rolled via `rng` rather
    # than `faker.date_of_birth` because faker draws share state with
    # name draws and would shift packet ids.
    dob_age_days = rng.randint(30 * 365, 70 * 365)
    dob = (_NEW_PACKET_ANCHOR - timedelta(days=dob_age_days)).isoformat()

    return PacketSpec(
        id=packet_id,
        label=f"Dr. {first} {last}",
        license_fields=LicenseFields(
            full_name=names.license,
            license_number=license_number,
            state=profile.license_state,
            issue_date=_date_str(license_issue),
            expiry_date=_date_str(license_expiry),
            status="Active",
            taxonomy_code=profile.taxonomy_code,
        ),
        dea_fields=DeaFields(
            full_name=names.dea,
            dea_number=dea_number,
            expiry_date=_date_str(dea_expiry),
            status="Active",
            schedules=("II", "III", "IV", "V"),
        ),
        board_cert_fields=BoardCertFields(
            full_name=names.board_cert,
            board=board,
            specialty=profile.primary_specialty,
            issue_date=_date_str(board_issue),
            expiry_date=_date_str(board_expiry),
            status="Active",
        ),
        malpractice_fields=MalpracticeFields(
            full_name=names.malpractice,
            carrier=carrier,
            policy_number=policy_number,
            expiry_date=_date_str(malpractice_exp),
            status="Active",
            licensee_license_number=license_number,
            licensee_license_expiry=_date_str(license_expiry),
        ),
        identity_fields=IdentityFields(
            full_name=f"{first} {last}",
            npi=profile.npi,
            date_of_birth=dob,
            credentialing_state=profile.license_state,
        ),
        clean_pattern=clean_pattern,
        notes=(
            f"P4 programmatic ({tag}). "
            f"NPPES taxonomy {profile.taxonomy_code} / {profile.primary_specialty}."
        ),
    )


def build_new_packets() -> list[PacketSpec]:
    """45 programmatically generated P4 packets across the 6 sub-buckets.

    Order is deterministic from `_NEW_PACKET_SEED`: sampler draws 45 NPPES
    profiles, the loop walks them in sampler order, and each spec's per-packet
    Random uses a derived seed so adding a bucket doesn't shift downstream
    packets' field values.
    """
    profiles = sample_n(_NEW_PACKET_SEED, _TOTAL_NEW_PACKETS)
    faker = faker_for(_NEW_PACKET_SEED)

    specs: list[PacketSpec] = []
    profile_iter = iter(profiles)
    packet_idx = 6  # P2 occupies 001..005

    # Separate counter for name-conflict packets so ConflictShape rotates
    # independently of packet_idx — keeps each shape appearing roughly
    # (10 name-conflict packets / 5 shapes) = 2 times across the bucket plan.
    name_conflict_idx = 0
    clean_patterns = list(CleanNamePattern)
    conflict_shapes = list(ConflictShape)

    for bucket in _BUCKET_PLAN:
        for _ in range(bucket.count):
            profile = next(profile_iter)
            # Per-packet RNG derived from the master seed + packet index. Adding
            # a bucket re-routes packet indices but keeps each packet's local
            # rng stable for its own field generation.
            packet_rng = Random(_NEW_PACKET_SEED * _PACKET_RNG_STRIDE + packet_idx)
            # Clean pattern rotates by packet_idx so every pattern appears in
            # both clean buckets and conflict buckets — no FP category is
            # confined to packets the LLM never has to learn the clean shape of.
            clean_pattern = clean_patterns[packet_idx % len(clean_patterns)]
            spec = _spec_from_profile(
                packet_idx, profile, faker, packet_rng,
                tag=bucket.slug, clean_pattern=clean_pattern,
            )
            if bucket.conflict == "name":
                shape = conflict_shapes[name_conflict_idx % len(conflict_shapes)]
                spec = plant_name_variant(spec, packet_rng, shape=shape)
                name_conflict_idx += 1
            elif bucket.conflict == "taxonomy":
                spec = plant_taxonomy_specialty_mismatch(spec, packet_rng)
            if bucket.scanned:
                spec = replace(spec, scanned=True)
            specs.append(spec)
            packet_idx += 1

    return specs


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Generate PacketReady eval packets.")
    parser.add_argument(
        "output_root",
        type=Path,
        help="Directory to write packets into (e.g. evals/dataset/).",
    )
    parser.add_argument(
        "--no-manifest",
        action="store_true",
        help="Skip writing evals/tuning_subsets.json. Use only for dataset-only "
             "experiments where the runners/ module isn't on the path.",
    )
    args = parser.parse_args(argv)
    generate_all(args.output_root)
    print(f"wrote {len(all_specs())} packets into {args.output_root}")

    if not args.no_manifest:
        # Late import to break the runners/ ↔ packetready_eval/ cycle.
        # tuning_subsets re-derives the tuples on import, so this works the
        # moment we've written the dataset.
        try:
            from runners.tuning_subsets import write_manifest
        except ImportError:
            print(
                "  (skipping tuning_subsets.json — runners/ not on sys.path; "
                "run `pip install -e evals/runners` or invoke "
                "`python -m runners.tuning_subsets --write` separately)"
            )
        else:
            # output_root is conventionally `<repo>/evals/dataset/`; the
            # repo root is two levels up. Resolve before stepping so symlinks
            # and trailing slashes don't trip the parents lookup.
            repo_root = args.output_root.resolve().parents[1]
            manifest_path = write_manifest(repo_root)
            print(f"wrote {manifest_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
