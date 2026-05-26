"""
Conflict planters for P4. Each planter takes a clean :class:`PacketSpec` and
returns a NEW spec carrying a disagreement plus the matching
``plantedConflicts`` marker.

Both ends — the rendered PDFs and the conflict marker the runner reads — come
out of the same mutation, so drift between the two is impossible. The
``conflict_metrics.py`` runner uses the marker to compute per-kind
precision/recall against validator-emitted Issues.

P4 plants two kinds:

  - ``name_variant`` (5 shapes — see :class:`ConflictShape`): malpractice's
    fullName diverges from license/dea/board cert. Four shapes are real
    disagreements (``expected_to_flag=True``); one (``SURNAME_TYPO``) is a
    one-character typo the validator must NOT flag (``expected_to_flag=False``).
    Caught by :pyattr:`identity_coherence`. Target field: ``malpractice.fullName``.

  - ``taxonomy_specialty_mismatch``: license carries specialty A's NUCC
    taxonomy code, board cert states specialty B (with B's board acronym,
    so the board cert remains internally consistent). The deterministic
    NUCC lookup + thin Sonnet semantic-match call in
    :pyattr:`npi_taxonomy_match` flags it. Always
    ``expected_to_flag=True``. Target field: ``boardCert.specialty``.

``expiry_mismatch`` is intentionally absent — see the P4 doc "Conflict kinds
in P4" decision. The validator that catches it (:pyattr:`ExpiryConsistencyValidator`)
ships in Phase 4.5.

Marker fields:
  - ``kind`` — the planted conflict kind (matches validator name expectations).
  - ``field`` — ``docType.fieldName`` form, matches :data:`schema.PER_FIELD_KEYS`.
  - ``sources`` — doc types involved in the disagreement.
  - ``description`` — human-readable.
  - ``expectedSeverity`` — what the validator should emit when it flags.
  - ``expected_to_flag`` — True for shapes the FP/recall gate scores as
    must-catch; False for the typo-tolerance shape.
  - ``shape`` (name_variant only) — the :class:`ConflictShape` name, so the
    metrics counter can break recall down per-shape.
"""

from __future__ import annotations

from dataclasses import replace
from random import Random
from typing import TYPE_CHECKING

from .name_variants import (
    EXPECTED_TO_FLAG,
    ConflictShape,
    malpractice_variant_for_shape,
)
from .specialty_catalog import SPECIALTY_CATALOG, mismatch_safe_specialties

# Imported only for typing. Runtime body uses `dataclasses.replace`, which is
# duck-typed against frozen dataclasses — no need to pull PacketSpec at import
# time, and avoiding it breaks an otherwise-real packets.py ↔ planters cycle.
if TYPE_CHECKING:
    from .packets import PacketSpec


def plant_name_variant(
    spec: "PacketSpec",
    rng: Random,
    *,
    shape: ConflictShape = ConflictShape.HYPHENATED_SUFFIX,
) -> "PacketSpec":
    """
    Append a name_variant disagreement: malpractice.full_name diverges from
    the other three docs per `shape`. License/DEA/board cert stay as the
    clean baseline. Returns a new :class:`PacketSpec` (the original is frozen).

    Default shape is HYPHENATED_SUFFIX so pre-task-9 tests and any external
    caller without a shape parameter continue to plant the original P4 conflict.

    The (first, last) used to construct the variant is parsed from the license
    name. The license name's credential suffix (", MD", ", M.D.", or bare) is
    preserved on the variant so the disagreement is ONLY on the name, not also
    on the credential — that would conflate name_variant with credential-suffix
    FP material.
    """
    license_name = spec.license_fields.full_name
    base, license_suffix = _split_name_and_suffix(license_name)
    parts = base.split()
    if len(parts) < 2:
        raise ValueError(
            f"plant_name_variant: license name {license_name!r} lacks both a first and last name"
        )
    first, last = parts[0], parts[-1]

    variant = malpractice_variant_for_shape(
        shape, first, last, rng, license_suffix=license_suffix,
    )

    return replace(
        spec,
        malpractice_fields=replace(spec.malpractice_fields, full_name=variant),
        planted_conflicts=[
            *spec.planted_conflicts,
            {
                "kind": "name_variant",
                "shape": shape.name,
                "field": "malpractice.fullName",
                "sources": ["license", "malpractice"],
                "description": f"license: {license_name!r}; malpractice: {variant!r}",
                "expectedSeverity": "Critical",
                "expected_to_flag": EXPECTED_TO_FLAG[shape],
            },
        ],
    )


def plant_taxonomy_specialty_mismatch(spec: "PacketSpec", rng: Random) -> "PacketSpec":
    """
    Append a taxonomy_specialty_mismatch: license's taxonomy_code points at
    specialty A; board cert states specialty B (A ≠ B). The board cert's
    ``board`` acronym is updated to B's certifying board so the board cert
    PDF stays internally consistent — the only cross-doc disagreement is
    license-vs-board on specialty. DEA and malpractice are untouched.

    Always ``expected_to_flag=True`` (no typo-tolerance equivalent for
    NUCC codes; either the code points to the right classification or it doesn't).
    """
    pool = mismatch_safe_specialties()
    a_name, b_name = rng.sample(pool, 2)
    a_info = SPECIALTY_CATALOG[a_name]
    b_info = SPECIALTY_CATALOG[b_name]

    return replace(
        spec,
        license_fields=replace(spec.license_fields, taxonomy_code=a_info.taxonomy_code),
        board_cert_fields=replace(
            spec.board_cert_fields,
            specialty=b_name,
            board=b_info.board_acronym,
        ),
        planted_conflicts=[
            *spec.planted_conflicts,
            {
                "kind": "taxonomy_specialty_mismatch",
                "field": "boardCert.specialty",
                "sources": ["license", "boardCert"],
                "description": (
                    f"license taxonomy_code {a_info.taxonomy_code} → canonical specialty {a_name!r}; "
                    f"board cert specialty {b_name!r} ({b_info.board_acronym})"
                ),
                "expectedSeverity": "Critical",
                "expected_to_flag": True,
            },
        ],
    )


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------


# Credential suffixes the planter recognizes on a license name. Ordered
# longest-first so ", M.D." matches before ", MD" (otherwise the shorter
# match would steal the period and leave the variant unbalanced).
_KNOWN_CREDENTIAL_SUFFIXES: tuple[str, ...] = (
    ", M.D.", ", MD", ", DO", ", D.O.",
)


def _split_name_and_suffix(license_name: str) -> tuple[str, str]:
    """Split a license fullName into (bare_name, credential_suffix).

    Returns ('', '') for inputs that don't look like a name. The credential
    suffix includes the leading comma so the variant can paste it back
    verbatim (or omit it for the bare DEA-style names).
    """
    for sfx in _KNOWN_CREDENTIAL_SUFFIXES:
        if license_name.endswith(sfx):
            return license_name[: -len(sfx)].strip(), sfx
    # No recognized credential — return the whole string as the bare name.
    return license_name.strip(), ""
