"""Shared layout primitives for credentialing-doc PDFs.

Every doc type renders the same chrome: title band, issuing-body subtitle,
two-column label/value grid, signature footer. Variation lives in content,
not chrome — extractor robustness to chrome variation is a P4 problem.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass

from reportlab.lib import colors
from reportlab.lib.pagesizes import LETTER
from reportlab.lib.units import inch
from reportlab.pdfgen.canvas import Canvas

PAGE_W, PAGE_H = LETTER

# Margins picked to give a comfortable extractor target: wide enough that
# Sonnet vision doesn't see edge clipping, narrow enough that the body grid
# doesn't drown the title band.
MARGIN_X = 0.9 * inch
MARGIN_TOP = 0.9 * inch

FONT_REG = "Helvetica"
FONT_BOLD = "Helvetica-Bold"


@dataclass(frozen=True)
class HeaderSpec:
    issuing_body: str          # "New York State Education Department"
    title: str                 # "Physician License"
    badge: str                 # "OFFICIAL DOCUMENT"


def draw_header(c: Canvas, h: HeaderSpec) -> float:
    """Draws the title band; returns the y-coordinate where the body should start."""
    band_top = PAGE_H - MARGIN_TOP
    band_height = 1.05 * inch

    # Band background — pale gray so the title pops without fighting the body.
    c.setFillColorRGB(0.93, 0.94, 0.97)
    c.rect(MARGIN_X, band_top - band_height, PAGE_W - 2 * MARGIN_X,
           band_height, fill=1, stroke=0)

    # Issuing body, small caps feel via the smaller font.
    c.setFillColor(colors.black)
    c.setFont(FONT_BOLD, 11)
    c.drawString(MARGIN_X + 0.2 * inch, band_top - 0.32 * inch, h.issuing_body)

    # Title.
    c.setFont(FONT_BOLD, 20)
    c.drawString(MARGIN_X + 0.2 * inch, band_top - 0.7 * inch, h.title)

    # Badge, right-aligned.
    c.setFont(FONT_BOLD, 9)
    badge_w = c.stringWidth(h.badge, FONT_BOLD, 9)
    badge_x = PAGE_W - MARGIN_X - 0.2 * inch - badge_w - 0.2 * inch
    badge_y = band_top - 0.4 * inch
    c.setFillColorRGB(0.15, 0.25, 0.55)
    c.roundRect(badge_x, badge_y, badge_w + 0.4 * inch, 0.25 * inch,
                0.06 * inch, fill=1, stroke=0)
    c.setFillColor(colors.white)
    c.drawString(badge_x + 0.2 * inch, badge_y + 0.075 * inch, h.badge)

    c.setFillColor(colors.black)
    # Cast because `inch` from unstubbed reportlab makes the arithmetic
    # produce Any; mypy-strict wants the return type narrowed.
    return float(band_top - band_height - 0.3 * inch)


def draw_field_grid(
    c: Canvas,
    y_start: float,
    rows: Iterable[tuple[str, str]],
    *,
    label_width: float = 1.9 * inch,
    row_height: float = 0.32 * inch,
) -> float:
    """Two-column label/value grid. Returns y-coordinate after the last row.

    Labels render bold; values regular. Each row gets a hairline rule beneath
    it — gives the OCR something stable to anchor on without being chrome-heavy.
    """
    x_label = MARGIN_X
    x_value = MARGIN_X + label_width
    y = y_start

    for label, value in rows:
        c.setFont(FONT_BOLD, 10)
        c.drawString(x_label, y, label)
        c.setFont(FONT_REG, 11)
        c.drawString(x_value, y, value)
        y -= row_height * 0.5
        c.setStrokeColorRGB(0.85, 0.85, 0.88)
        c.setLineWidth(0.4)
        c.line(MARGIN_X, y, PAGE_W - MARGIN_X, y)
        y -= row_height * 0.5

    return y


def draw_footer(c: Canvas, *, authority: str, signature: str) -> None:
    """Signature line + authority caption near the bottom of the page."""
    y = 1.2 * inch
    c.setStrokeColor(colors.black)
    c.setLineWidth(0.6)
    c.line(MARGIN_X, y, MARGIN_X + 3 * inch, y)

    c.setFont(FONT_REG, 10)
    c.drawString(MARGIN_X, y - 0.18 * inch, signature)
    c.setFont(FONT_REG, 8)
    c.drawString(MARGIN_X, y - 0.36 * inch, authority)
