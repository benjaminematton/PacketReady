"""
Single source of truth for (specialty → NUCC taxonomy code → board acronym).

Two consumers:

  - the programmatic packet builder in :mod:`packetready_eval.packets` looks
    up a board acronym given an NPPES-sampled primary specialty.
  - :mod:`packetready_eval.conflict_planters` picks two mismatch-safe entries
    when planting a ``taxonomy_specialty_mismatch`` conflict. "Mismatch-safe"
    means the taxonomy code is a NUCC base code whose canonical specialty
    isn't a subspec rollup of any other entry's specialty — otherwise the
    validator can reasonably accept (A, B) as synonymous and the planted
    conflict goes unflagged.

The dataclass form lets the planter update both ``license.taxonomyCode`` AND
``boardCert.board`` from one lookup, so a board-cert PDF never lists a board
that doesn't certify the specialty printed on it.
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class SpecialtyInfo:
    taxonomy_code: str        # NUCC code, empty if not used as a license-side value
    board_acronym: str        # American board that certifies this specialty
    mismatch_safe: bool = False


# (specialty → SpecialtyInfo). Entries flagged ``mismatch_safe=True`` carry
# a NUCC base code with no subspec rollups in this table — those are the only
# specialties the conflict planter samples from.
SPECIALTY_CATALOG: dict[str, SpecialtyInfo] = {
    "Internal Medicine":                  SpecialtyInfo("207R00000X", "ABIM", mismatch_safe=True),
    "Family Medicine":                    SpecialtyInfo("207Q00000X", "ABFM", mismatch_safe=True),
    "Emergency Medicine":                 SpecialtyInfo("207P00000X", "ABEM", mismatch_safe=True),
    "Pediatrics":                         SpecialtyInfo("208000000X", "ABP",  mismatch_safe=True),
    "Anesthesiology":                     SpecialtyInfo("207L00000X", "ABA",  mismatch_safe=True),
    "Dermatology":                        SpecialtyInfo("207N00000X", "ABD",  mismatch_safe=True),
    "Ophthalmology":                      SpecialtyInfo("207W00000X", "ABO",  mismatch_safe=True),
    "Urology":                            SpecialtyInfo("208800000X", "ABU",  mismatch_safe=True),
    "Psychiatry & Neurology":             SpecialtyInfo("", "ABPN"),
    "Obstetrics & Gynecology":            SpecialtyInfo("", "ABOG"),
    "Surgery":                            SpecialtyInfo("", "ABS"),
    "Radiology":                          SpecialtyInfo("", "ABR"),
    "Orthopaedic Surgery":                SpecialtyInfo("", "ABOS"),
    "Pathology":                          SpecialtyInfo("", "ABPath"),
    "Otolaryngology":                     SpecialtyInfo("", "ABOto"),
    "Physical Medicine & Rehabilitation": SpecialtyInfo("", "ABPMR"),
    "Plastic Surgery":                    SpecialtyInfo("", "ABPS"),
    "Neurological Surgery":               SpecialtyInfo("", "ABNS"),
    "Allergy & Immunology":               SpecialtyInfo("", "ABAI"),
    "Colon & Rectal Surgery":             SpecialtyInfo("", "ABCRS"),
    "Thoracic Surgery (Cardiothoracic Vascular Surgery)": SpecialtyInfo("", "ABTS"),
    "Nuclear Medicine":                   SpecialtyInfo("", "ABNM"),
    "Preventive Medicine":                SpecialtyInfo("", "ABPM"),
    "Medical Genetics":                   SpecialtyInfo("", "ABMG"),
}

# Umbrella body. Used when an NPPES-sampled specialty isn't in the catalog —
# the board cert PDF still renders, just under the ABMS umbrella name.
FALLBACK_BOARD_ACRONYM = "ABMS"


def board_for_specialty(specialty: str) -> str:
    """Board acronym for `specialty`, or :data:`FALLBACK_BOARD_ACRONYM`."""
    info = SPECIALTY_CATALOG.get(specialty)
    return info.board_acronym if info else FALLBACK_BOARD_ACRONYM


def mismatch_safe_specialties() -> tuple[str, ...]:
    """Specialty names safe to pick as the (A, B) endpoints of a mismatch."""
    return tuple(s for s, info in SPECIALTY_CATALOG.items() if info.mismatch_safe)
