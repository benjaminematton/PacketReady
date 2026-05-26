"""Provider-identity contract: dataclass, NPI Luhn helpers, and wire validator.

Lives in its own module (rather than ``packets.py``) so the runner-side
preflight in ``evals/runners/runners/golden.py`` can import the validator
without pulling in the PDF renderer chain (reportlab, faker) that
``packets.py`` drags along.

Three independent implementations of the same Luhn check exist by design:

  1. :func:`npi_check_digit` — the generator-side check-digit *computer*.
     Hand-crafted packets call it to build an NPI from a 9-digit base.
  2. :func:`is_npi_luhn_valid` — the wire validator below, used by both
     the runner preflight and the C# ``ProviderIdentityValidator``.
  3. The test re-implementation in ``tests/test_identity_contract.py``.

The test asserts (1) and (2) agree, and that both pass an independent
fourth implementation (3). A regression in any one surface is loud.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import date
from typing import Any

# Bumped to 2 by P4 slice 2 — adds top-level ``identity`` block. The
# runner-side preflight hard-fails on an unknown version so the dataset
# can't silently mix v1 and v2 goldens.
GOLDEN_SCHEMA_VERSION: int = 2


@dataclass(frozen=True)
class IdentityFields:
    """
    Provider identity (P4 task 1, slice 2). These are NOT extracted from any
    P4 document type — they live on the Provider row at create time and get
    overlaid by the aggregator at score time. The CV extractor (not shipped)
    will close this seam in a later phase; until then, the eval orchestrator
    has to pass them explicitly to ``POST /api/providers``.

    All four fields are required when emitted to golden.json. The
    orchestrator's wire validator (``ProviderIdentityValidator`` on the C#
    side) re-validates: NPI is CMS-Luhn-valid (10 digits, "80840" prefix +
    mod-10), state matches ``^[A-Z]{2}$``, DOB is in
    [1900-01-02, today], fullName non-empty.
    """
    full_name: str
    npi: str                     # 10 digits, CMS-Luhn-valid
    date_of_birth: str           # YYYY-MM-DD
    credentialing_state: str     # 2 uppercase letters


# --- NPI Luhn helpers --------------------------------------------------------


def npi_check_digit(base9: str) -> str:
    """Compute the CMS NPI check digit (10th position) for a 9-digit base.

    The 9-digit base is prefixed with the ISO 7812 issuer identifier "80840";
    standard Luhn-mod-10 over the 14-digit string yields the check digit.
    The 10-digit NPI = base9 + check_digit then satisfies the full Luhn
    validator on "80840"+npi.
    """
    if len(base9) != 9 or not base9.isdigit():
        raise ValueError(f"npi_check_digit: base9 must be 9 digits, got {base9!r}")
    prefixed = "80840" + base9
    total = 0
    for i, c in enumerate(reversed(prefixed)):
        d = int(c)
        if i % 2 == 0:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return str((10 - total % 10) % 10)


def npi_from_base(base9: str) -> str:
    """Build a CMS-Luhn-valid 10-digit NPI from a 9-digit base."""
    return base9 + npi_check_digit(base9)


def is_npi_luhn_valid(npi: str) -> bool:
    """Mirror of the C# ``ProviderIdentityValidator.IsNpiLuhnValid``.

    Returns False on any non-digit or non-10-character input (rather than
    raising), so a malformed payload can be reported as a wire-shape
    violation alongside other contract errors.
    """
    if not isinstance(npi, str) or len(npi) != 10 or not npi.isdigit():
        return False
    prefixed = "80840" + npi
    total = 0
    for i, c in enumerate(reversed(prefixed)):
        d = int(c)
        if i % 2 == 1:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return total % 10 == 0


# --- Wire-format validation --------------------------------------------------


_STATE_RE = re.compile(r"^[A-Z]{2}$")
_DOB_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
_MIN_DOB: date = date(1900, 1, 2)

IDENTITY_KEYS: tuple[str, ...] = (
    "fullName", "npi", "dateOfBirth", "credentialingState",
)


def validate_identity_dict(identity: Any, *, today: date | None = None) -> list[str]:
    """Return human-readable contract violations for ``identity``.

    Mirrors the C# ``ProviderIdentityValidator.Validate`` shape: ``[]`` on
    success, one entry per violation otherwise. ``today`` defaults to
    ``date.today()``; callers can pin it for deterministic tests.
    """
    if today is None:
        today = date.today()

    if not isinstance(identity, dict):
        return [f"identity must be an object; got {type(identity).__name__}"]

    missing = [k for k in IDENTITY_KEYS if k not in identity]
    if missing:
        # Bail early — every downstream check assumes the key is present.
        return [f"identity missing keys {missing}"]

    errors: list[str] = []

    full_name = identity["fullName"]
    if not isinstance(full_name, str) or not full_name.strip():
        errors.append("identity.fullName must be a non-empty string")

    npi = identity["npi"]
    if not isinstance(npi, str) or len(npi) != 10 or not npi.isdigit():
        errors.append(f"identity.npi must be exactly 10 digits; got {npi!r}")
    elif not is_npi_luhn_valid(npi):
        errors.append(f"identity.npi {npi!r} failed the CMS Luhn check")

    dob_raw = identity["dateOfBirth"]
    if not isinstance(dob_raw, str) or not _DOB_RE.fullmatch(dob_raw):
        errors.append(f"identity.dateOfBirth must be YYYY-MM-DD; got {dob_raw!r}")
    else:
        try:
            dob = date.fromisoformat(dob_raw)
        except ValueError:
            errors.append(f"identity.dateOfBirth {dob_raw!r} is not a valid date")
        else:
            if dob < _MIN_DOB:
                errors.append(
                    f"identity.dateOfBirth {dob} must be on or after {_MIN_DOB}"
                )
            if dob > today:
                errors.append(
                    f"identity.dateOfBirth {dob} must not be in the future "
                    f"(today is {today})"
                )

    state = identity["credentialingState"]
    if not isinstance(state, str) or not _STATE_RE.fullmatch(state):
        errors.append(
            f"identity.credentialingState must match ^[A-Z]{{2}}$; got {state!r}"
        )

    return errors
