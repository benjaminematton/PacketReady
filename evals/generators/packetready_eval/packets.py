"""Generate all 5 P2 packets from a single Python literal.

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
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .docs.board_cert_pdf import BoardCertFields, render as render_board_cert
from .docs.dea_pdf import DeaFields, render as render_dea
from .docs.license_pdf import LicenseFields, render as render_license
from .docs.malpractice_pdf import MalpracticeFields, render as render_malpractice
from .scan_artifacts import rasterize_and_degrade
from .schema import BOARD_CERT, DEA, DOC_FILENAMES, LICENSE, MALPRACTICE


@dataclass(frozen=True)
class PacketSpec:
    id: str
    label: str
    license_fields: LicenseFields
    dea_fields: DeaFields
    board_cert_fields: BoardCertFields
    malpractice_fields: MalpracticeFields
    planted_conflicts: list[dict[str, Any]] = field(default_factory=list)
    notes: str = ""
    scanned: bool = False        # P005 only — rasterize+degrade after rendering


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


PACKET_SPECS: list[PacketSpec] = [
    # 001 — clean, Anderson, NY, Internal Medicine.
    PacketSpec(
        id="packet-001-clean-anderson",
        label="Dr. Henry Anderson",
        license_fields=_ANDERSON_LICENSE,
        dea_fields=_ANDERSON_DEA,
        board_cert_fields=_ANDERSON_BOARD,
        malpractice_fields=_ANDERSON_MALPRACTICE,
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
        planted_conflicts=[
            {
                "kind": "name_variant",
                "sources": ["license", "malpractice"],
                "description": "license: 'Jane Calloway, MD'; malpractice: 'Jane C. Calloway-Smith, MD'",
                "expectedSeverity": "Critical",
            },
        ],
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
        planted_conflicts=[
            {
                "kind": "expiry_mismatch",
                "field": "license.expiryDate",
                "sources": ["license", "malpractice"],
                "description": "license.pdf shows expiry 2026-09-30; malpractice.pdf's Licensee footer records expiry 2027-09-30 for the same license number 036-IL-58031",
                "expectedSeverity": "Critical",
            },
        ],
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


def golden_for(spec: PacketSpec) -> dict[str, Any]:
    """Build the golden.json dict for a single packet spec.

    Exposed (not underscored) because the schema-invariant test loads it
    directly without invoking the PDF renderers.
    """
    return {
        "packetId": spec.id,
        "label": spec.label,
        "documents": [
            {"type": LICENSE,     "filename": DOC_FILENAMES[LICENSE],     "fields": _license_json(spec.license_fields)},
            {"type": DEA,         "filename": DOC_FILENAMES[DEA],         "fields": _dea_json(spec.dea_fields)},
            {"type": BOARD_CERT,  "filename": DOC_FILENAMES[BOARD_CERT],  "fields": _board_cert_json(spec.board_cert_fields)},
            {"type": MALPRACTICE, "filename": DOC_FILENAMES[MALPRACTICE], "fields": _malpractice_json(spec.malpractice_fields)},
        ],
        "plantedConflicts": spec.planted_conflicts,
        "notes": spec.notes,
    }


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


def generate_all(output_root: Path) -> None:
    """Idempotent: wipes each packet directory before regenerating.

    Per-packet progress is printed because scanned packets take multiple
    seconds (200 dpi rasterization × 4 docs) and silent output reads as a
    hang in CI logs.
    """
    output_root.mkdir(parents=True, exist_ok=True)
    total = len(PACKET_SPECS)
    for idx, spec in enumerate(PACKET_SPECS, start=1):
        suffix = " (scanned)" if spec.scanned else ""
        print(f"  [{idx}/{total}] {spec.id}{suffix}", flush=True)
        packet_dir = output_root / spec.id
        if packet_dir.exists():
            shutil.rmtree(packet_dir)
        _write_packet(packet_dir, spec)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Generate PacketReady eval packets.")
    parser.add_argument(
        "output_root",
        type=Path,
        help="Directory to write packets into (e.g. evals/dataset/).",
    )
    args = parser.parse_args(argv)
    generate_all(args.output_root)
    print(f"wrote {len(PACKET_SPECS)} packets into {args.output_root}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
