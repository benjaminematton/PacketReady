"""Rasterize + degrade a clean PDF into a fax-pipeline-style scanned PDF.

Mimics the artifacts Phase 3's Sonnet vision needs to be robust against:
small skew, JPEG quantization, mild downsampling. No random noise in P2 —
that lands in P4 when the dataset scales.

Reproducibility: same source + same params produces a visually-identical
PDF. JPEG quantization is not guaranteed byte-identical across Pillow
versions, so Pillow and PyMuPDF are pinned in pyproject.toml. Pinning
keeps `git diff evals/dataset/` quiet until the pin moves.
"""

from __future__ import annotations

import io
from pathlib import Path

import fitz  # PyMuPDF
from PIL import Image
from reportlab.lib.pagesizes import LETTER
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen.canvas import Canvas


def rasterize_and_degrade(
    src_pdf: Path,
    dst_pdf: Path,
    *,
    dpi: int = 200,
    skew_degrees: float = 0.7,
    jpeg_quality: int = 70,
) -> None:
    """Render each page to a JPEG at `dpi`, rotate by `skew_degrees`,
    re-pack into a new PDF at LETTER size.

    The rotation expands the canvas (`expand=True`) so corners aren't
    clipped; the result is centered on the LETTER page so the visible
    document still fits the printable area.
    """
    page_w_pt, page_h_pt = LETTER

    # `fitz.open` and the Canvas constructor are both opened inside the `with`
    # so a failure in either (permission denied, malformed source) closes the
    # other deterministically. PyMuPDF supports the context-manager protocol.
    with fitz.open(str(src_pdf)) as src:
        out = Canvas(str(dst_pdf), pagesize=LETTER)
        for page in src:
            zoom = dpi / 72.0
            matrix = fitz.Matrix(zoom, zoom)
            pixmap = page.get_pixmap(matrix=matrix, alpha=False)
            img = Image.frombytes("RGB", (pixmap.width, pixmap.height), pixmap.samples)

            # Skew: white-fill the rotated background so the JPEG round-trip
            # doesn't turn the page corners black.
            img = img.rotate(
                skew_degrees,
                resample=Image.Resampling.BICUBIC,
                expand=True,
                fillcolor=(255, 255, 255),
            )

            # JPEG round-trip via an in-memory buffer.
            buf = io.BytesIO()
            img.save(buf, format="JPEG", quality=jpeg_quality)
            buf.seek(0)

            # Center the image on the LETTER page, scaling to fit while
            # preserving aspect ratio.
            iw, ih = img.size
            scale = min(page_w_pt / iw, page_h_pt / ih)
            draw_w = iw * scale
            draw_h = ih * scale
            x = (page_w_pt - draw_w) / 2
            y = (page_h_pt - draw_h) / 2

            out.drawImage(ImageReader(buf), x, y, width=draw_w, height=draw_h)
            out.showPage()

        out.save()
