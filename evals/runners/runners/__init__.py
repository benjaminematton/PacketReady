"""Eval runners package.

The single source of truth for the local extractor base URL lives here so
the CLI default, the HTTP client default, and any future caller (CI, docs)
all read from the same constant. Keep in sync with the Phase-2 doc.
"""

from __future__ import annotations

DEFAULT_BASE_URL = "http://localhost:5066"

__all__ = ("DEFAULT_BASE_URL",)
