"""The authoritative dataset contract.

Single source of truth for:
  - which doc types exist
  - which fields each doc type scores against
  - what filename each doc type lives under in a packet directory

Both the generators (golden.json serializers in `packets.py`) and the runners
(`PER_FIELD_KEYS` in `metrics.py`) consume this module. Adding or renaming a
field happens in exactly one place; drift is impossible by construction.
"""

from __future__ import annotations

# Doc-type strings as they appear in golden.json `documents[].type` and as
# the `docType` form field in the extractor request.
LICENSE = "license"
DEA = "dea"
BOARD_CERT = "boardCert"
MALPRACTICE = "malpractice"

DOC_TYPES: tuple[str, ...] = (LICENSE, DEA, BOARD_CERT, MALPRACTICE)

# Per-packet filenames. Locked alongside the doc types so runners can
# resolve type → file without re-reading the spec.
DOC_FILENAMES: dict[str, str] = {
    LICENSE: "license.pdf",
    DEA: "dea.pdf",
    BOARD_CERT: "board-cert.pdf",
    MALPRACTICE: "malpractice.pdf",
}

# Scored fields per doc type, in the camelCase form that lands in golden.json.
# Order is presentation-only; comparison and rollup treat these as sets.
SCHEMA: dict[str, tuple[str, ...]] = {
    LICENSE: (
        "fullName",
        "licenseNumber",
        "state",
        "issueDate",
        "expiryDate",
        "status",
    ),
    DEA: (
        "fullName",
        "deaNumber",
        "expiryDate",
        "status",
        "schedules",
    ),
    BOARD_CERT: (
        "fullName",
        "board",
        "specialty",
        "issueDate",
        "expiryDate",
        "status",
    ),
    MALPRACTICE: (
        "fullName",
        "carrier",
        "policyNumber",
        "expiryDate",
        "status",
    ),
}

# Flat "docType.fieldName" keys, derived from SCHEMA — used by metrics to
# emit a stable per-field column set even when the extractor returns nothing.
PER_FIELD_KEYS: tuple[str, ...] = tuple(
    f"{doc_type}.{field}"
    for doc_type in DOC_TYPES
    for field in SCHEMA[doc_type]
)
