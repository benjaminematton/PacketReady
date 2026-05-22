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
    # Fail at generation time rather than silently emit `issuing_body=ABEM`
    # (the abbreviation) as if it were the full body name. Adding a new
    # board means extending _BOARD_NAMES — that's the whole point of the
    # map.
    if fields.board not in _BOARD_NAMES:
        raise ValueError(
            f"unknown board code {fields.board!r}; "
            f"add it to _BOARD_NAMES in {__file__}"
        )
    board_full = _BOARD_NAMES[fields.board]
    header = HeaderSpec(
        issuing_body=board_full,
        title="Diplomate Certification",
        badge="BOARD CERTIFIED",
    )

    c = Canvas(str(out), pagesize=LETTER)
    body_top = draw_header(c, header)
    # The PDF labels this "Recertification Due", but the scored golden key is
    # `expiryDate`. This is deliberate: it forces P3's extractor to do semantic
    # label mapping (the underlying credentialing concept is the same, the
    # printed wording varies across issuing bodies). Don't "fix" the label.
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
