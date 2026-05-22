"""Malpractice / professional liability certificate of insurance PDF.

The "Licensee Profile" footer block prints the underlying license number and
expiry the carrier wrote the policy against. The expiry there is the field a
P2 conflict packet uses to disagree with the license.pdf's own expiry —
two documents, both internally accurate, deliberately telling different
stories about the same license.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from reportlab.lib.pagesizes import LETTER
from reportlab.lib.units import inch
from reportlab.pdfgen.canvas import Canvas

from ._layout import (
    FONT_BOLD,
    MARGIN_X,
    PAGE_W,
    HeaderSpec,
    draw_field_grid,
    draw_footer,
    draw_header,
)


@dataclass(frozen=True)
class MalpracticeFields:
    full_name: str
    carrier: str
    policy_number: str
    expiry_date: str
    status: str
    # Licensee-footer block — printed on the PDF but NOT part of the scored
    # `fields` in golden.json. Used to plant cross-doc license-expiry conflicts.
    licensee_license_number: str | None = None
    licensee_license_expiry: str | None = None


def render(fields: MalpracticeFields, out: Path) -> None:
    header = HeaderSpec(
        issuing_body=fields.carrier,
        title="Certificate of Professional Liability Insurance",
        badge="POLICY CERTIFICATE",
    )

    c = Canvas(str(out), pagesize=LETTER)
    body_top = draw_header(c, header)
    grid_end = draw_field_grid(c, body_top, [
        ("Named Insured", fields.full_name),
        ("Carrier", fields.carrier),
        ("Policy Number", fields.policy_number),
        ("Policy Expiry", fields.expiry_date),
        ("Coverage Status", fields.status),
    ])

    if fields.licensee_license_number or fields.licensee_license_expiry:
        # Licensee Profile block — separate header so a human reader sees it
        # as cross-referenced license info, not as the policy's own metadata.
        y = grid_end - 0.3 * inch
        c.setFont(FONT_BOLD, 11)
        c.drawString(MARGIN_X, y, "Licensee Profile")
        y -= 0.05 * inch
        c.setStrokeColorRGB(0.2, 0.2, 0.3)
        c.setLineWidth(0.8)
        c.line(MARGIN_X, y, PAGE_W - MARGIN_X, y)
        y -= 0.25 * inch

        rows: list[tuple[str, str]] = []
        if fields.licensee_license_number:
            rows.append(("License Number on File", fields.licensee_license_number))
        if fields.licensee_license_expiry:
            rows.append(("License Expiry on File", fields.licensee_license_expiry))
        draw_field_grid(c, y, rows)

    draw_footer(
        c,
        authority=f"{fields.carrier} — Authorized Insurance Provider",
        signature="Underwriting Officer",
    )
    c.showPage()
    c.save()
