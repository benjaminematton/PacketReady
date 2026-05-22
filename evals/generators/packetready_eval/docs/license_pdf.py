"""State medical license PDF."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from reportlab.lib.pagesizes import LETTER
from reportlab.pdfgen.canvas import Canvas

from ._layout import HeaderSpec, draw_field_grid, draw_footer, draw_header


_STATE_NAMES = {
    "NY": "New York",
    "CA": "California",
    "IL": "Illinois",
    "TX": "Texas",
    "FL": "Florida",
}


@dataclass(frozen=True)
class LicenseFields:
    full_name: str
    license_number: str
    state: str
    issue_date: str          # ISO YYYY-MM-DD
    expiry_date: str
    status: str


def render(fields: LicenseFields, out: Path) -> None:
    """Emit a single-page state medical license."""
    state_full = _STATE_NAMES.get(fields.state, fields.state)
    header = HeaderSpec(
        issuing_body=f"{state_full} State Education Department",
        title="Physician License",
        badge="OFFICIAL DOCUMENT",
    )

    c = Canvas(str(out), pagesize=LETTER)
    body_top = draw_header(c, header)
    draw_field_grid(c, body_top, [
        ("Licensee", fields.full_name),
        ("License Number", fields.license_number),
        ("State of Issue", fields.state),
        ("Issue Date", fields.issue_date),
        ("Expiry Date", fields.expiry_date),
        ("Status", fields.status),
    ])
    draw_footer(
        c,
        authority=f"Office of the Professions, {state_full} Department of Education",
        signature="Director, Office of the Professions",
    )
    c.showPage()
    c.save()
