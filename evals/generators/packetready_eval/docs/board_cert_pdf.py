"""Board certification PDF (ABIM, ABFM, ABEM, etc.)."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from reportlab.lib.pagesizes import LETTER
from reportlab.pdfgen.canvas import Canvas

from ._layout import HeaderSpec, draw_field_grid, draw_footer, draw_header


_BOARD_NAMES = {
    "ABIM": "American Board of Internal Medicine",
    "ABFM": "American Board of Family Medicine",
    "ABEM": "American Board of Emergency Medicine",
    "ABMS": "American Board of Medical Specialties",
    "ABP": "American Board of Pediatrics",
}


@dataclass(frozen=True)
class BoardCertFields:
    full_name: str
    board: str                # short code, e.g. "ABIM"
    specialty: str
    issue_date: str
    expiry_date: str
    status: str


def render(fields: BoardCertFields, out: Path) -> None:
    board_full = _BOARD_NAMES.get(fields.board, fields.board)
    header = HeaderSpec(
        issuing_body=board_full,
        title="Diplomate Certification",
        badge="BOARD CERTIFIED",
    )

    c = Canvas(str(out), pagesize=LETTER)
    body_top = draw_header(c, header)
    draw_field_grid(c, body_top, [
        ("Diplomate", fields.full_name),
        ("Board", fields.board),
        ("Specialty", fields.specialty),
        ("Issue Date", fields.issue_date),
        ("Recertification Due", fields.expiry_date),
        ("Status", fields.status),
    ])
    draw_footer(
        c,
        authority=f"{board_full} — Member, American Board of Medical Specialties",
        signature="Chair, Credentials Committee",
    )
    c.showPage()
    c.save()
