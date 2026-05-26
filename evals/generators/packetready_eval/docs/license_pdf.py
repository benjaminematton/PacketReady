"""State medical license PDF."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from reportlab.lib.pagesizes import LETTER
from reportlab.pdfgen.canvas import Canvas

from ._layout import HeaderSpec, draw_field_grid, draw_footer, draw_header

# All 50 states + DC. Lookup falls back to the abbreviation if the state isn't
# here, so a typo only costs the header's "<State> State Education Department"
# verbosity, not a render failure.
_STATE_NAMES = {
    "AL": "Alabama",        "AK": "Alaska",         "AZ": "Arizona",
    "AR": "Arkansas",       "CA": "California",     "CO": "Colorado",
    "CT": "Connecticut",    "DE": "Delaware",       "DC": "District of Columbia",
    "FL": "Florida",        "GA": "Georgia",        "HI": "Hawaii",
    "ID": "Idaho",          "IL": "Illinois",       "IN": "Indiana",
    "IA": "Iowa",           "KS": "Kansas",         "KY": "Kentucky",
    "LA": "Louisiana",      "ME": "Maine",          "MD": "Maryland",
    "MA": "Massachusetts",  "MI": "Michigan",       "MN": "Minnesota",
    "MS": "Mississippi",    "MO": "Missouri",       "MT": "Montana",
    "NE": "Nebraska",       "NV": "Nevada",         "NH": "New Hampshire",
    "NJ": "New Jersey",     "NM": "New Mexico",     "NY": "New York",
    "NC": "North Carolina", "ND": "North Dakota",   "OH": "Ohio",
    "OK": "Oklahoma",       "OR": "Oregon",         "PA": "Pennsylvania",
    "RI": "Rhode Island",   "SC": "South Carolina", "SD": "South Dakota",
    "TN": "Tennessee",      "TX": "Texas",          "UT": "Utah",
    "VT": "Vermont",        "VA": "Virginia",       "WA": "Washington",
    "WV": "West Virginia",  "WI": "Wisconsin",      "WY": "Wyoming",
}


@dataclass(frozen=True)
class LicenseFields:
    full_name: str
    license_number: str
    state: str
    issue_date: str          # ISO YYYY-MM-DD
    expiry_date: str
    status: str
    # P4: NUCC taxonomy code printed on the license. The
    # `npi_taxonomy_match` validator (P4 task 10) checks that its canonical
    # specialty agrees with the board cert's stated specialty. Default exists
    # for the P3 grandfather window — every P4-shipped LicenseFields fills it.
    taxonomy_code: str = ""


def render(fields: LicenseFields, out: Path) -> None:
    """Emit a single-page state medical license."""
    state_full = _STATE_NAMES.get(fields.state, fields.state)
    header = HeaderSpec(
        issuing_body=f"{state_full} State Education Department",
        title="Physician License",
        badge="OFFICIAL DOCUMENT",
    )

    rows = [
        ("Licensee", fields.full_name),
        ("License Number", fields.license_number),
        ("State of Issue", fields.state),
        ("Issue Date", fields.issue_date),
        ("Expiry Date", fields.expiry_date),
        ("Status", fields.status),
    ]
    # Show taxonomy code only when populated — keeps pre-P4 PDFs visually
    # identical to before this field landed.
    if fields.taxonomy_code:
        rows.append(("NUCC Taxonomy Code", fields.taxonomy_code))

    c = Canvas(str(out), pagesize=LETTER)
    body_top = draw_header(c, header)
    draw_field_grid(c, body_top, rows)
    draw_footer(
        c,
        authority=f"Office of the Professions, {state_full} Department of Education",
        signature="Director, Office of the Professions",
    )
    c.showPage()
    c.save()
