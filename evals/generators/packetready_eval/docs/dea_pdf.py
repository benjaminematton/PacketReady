"""DEA controlled-substances registration certificate PDF."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from reportlab.lib.pagesizes import LETTER
from reportlab.pdfgen.canvas import Canvas

from ._layout import HeaderSpec, draw_field_grid, draw_footer, draw_header


@dataclass(frozen=True)
class DeaFields:
    full_name: str
    dea_number: str
    expiry_date: str
    status: str
    schedules: list[str]      # e.g. ["II", "III", "IV", "V"]


def render(fields: DeaFields, out: Path) -> None:
    header = HeaderSpec(
        issuing_body="U.S. Department of Justice — Drug Enforcement Administration",
        title="Controlled Substances Registration",
        badge="DEA REGISTRATION",
    )

    c = Canvas(str(out), pagesize=LETTER)
    body_top = draw_header(c, header)
    draw_field_grid(c, body_top, [
        ("Registrant", fields.full_name),
        ("DEA Number", fields.dea_number),
        ("Expiry Date", fields.expiry_date),
        ("Status", fields.status),
        ("Authorized Schedules", ", ".join(fields.schedules)),
    ])
    draw_footer(
        c,
        authority="Drug Enforcement Administration, Diversion Control Division",
        signature="Administrator, U.S. Drug Enforcement Administration",
    )
    c.showPage()
    c.save()
