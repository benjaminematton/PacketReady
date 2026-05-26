"""Identity-block contract pins (slice 2 of the orchestrator unblock chain).

The orchestrator passes ``identity`` to ``POST /api/providers`` whose
boundary validator (C# ``ProviderIdentityValidator``) enforces:

  - fullName non-empty
  - NPI: 10 digits + CMS Luhn-mod-10 against "80840" prefix
  - dateOfBirth: ISO YYYY-MM-DD, in [1900-01-02, today]
  - credentialingState: ``^[A-Z]{2}$``

A generator that emits an identity that fails the wire validator would
trip the orchestrator on every packet of the next eval run. Catching
the contract drift here keeps the runner-side smoke surface narrow.
"""

from __future__ import annotations

import re
from datetime import date

from packetready_eval.packets import (
    GOLDEN_SCHEMA_VERSION,
    all_specs,
    golden_for,
    npi_check_digit,
    npi_from_base,
)


_STATE_RE = re.compile(r"^[A-Z]{2}$")
_DOB_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
_MIN_DOB = date(1900, 1, 2)


def _is_luhn_valid_npi(npi: str) -> bool:
    """Mirror of the C# ProviderIdentityValidator.IsNpiLuhnValid. The
    test re-implements the check so a drift in the helper that
    generates NPIs (npi_check_digit) doesn't silently validate itself."""
    if len(npi) != 10 or not npi.isdigit():
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


def test_golden_schema_version_is_2():
    # Bumped in slice 2; downstream readers (orchestrator + agreement
    # runner) can branch on this if they ever need to support older
    # goldens. Pin so a regen at v1 fails loud rather than silently.
    assert GOLDEN_SCHEMA_VERSION == 2


def test_every_packet_emits_an_identity_block():
    for spec in all_specs():
        golden = golden_for(spec)
        assert "identity" in golden, f"{spec.id} missing identity block"
        identity = golden["identity"]
        assert set(identity.keys()) == {
            "fullName", "npi", "dateOfBirth", "credentialingState",
        }, f"{spec.id}: identity has unexpected keys {set(identity.keys())}"


def test_every_identity_passes_wire_validator():
    for spec in all_specs():
        identity = golden_for(spec)["identity"]
        pid = spec.id

        assert identity["fullName"] and identity["fullName"].strip(), (
            f"{pid}: fullName must be non-blank")

        assert _STATE_RE.fullmatch(identity["credentialingState"]), (
            f"{pid}: credentialingState {identity['credentialingState']!r} "
            f"fails ^[A-Z]{{2}}$")

        assert _DOB_RE.fullmatch(identity["dateOfBirth"]), (
            f"{pid}: dateOfBirth {identity['dateOfBirth']!r} fails YYYY-MM-DD")
        dob = date.fromisoformat(identity["dateOfBirth"])
        assert _MIN_DOB <= dob <= date.today(), (
            f"{pid}: dateOfBirth {dob} outside [{_MIN_DOB}, today]")

        assert _is_luhn_valid_npi(identity["npi"]), (
            f"{pid}: NPI {identity['npi']!r} fails CMS Luhn check")


def test_npi_from_base_round_trips_through_validator():
    # Sanity for the generator helper. The validator above is independent
    # of the helper, so this catches a regression where the helper
    # computes the wrong check digit even though the validator is right.
    samples = ["100000001", "199900001", "123456789", "987654321"]
    for base in samples:
        npi = npi_from_base(base)
        assert len(npi) == 10
        assert npi[:9] == base
        assert _is_luhn_valid_npi(npi), f"{npi} (base={base}) failed Luhn"


def test_npi_check_digit_rejects_bad_input():
    import pytest
    with pytest.raises(ValueError):
        npi_check_digit("12345")        # too short
    with pytest.raises(ValueError):
        npi_check_digit("1234abcde")    # non-digits


def test_p4_programmatic_npis_match_nppes_source():
    # Programmatic packets pull NPI from SampledProfile (NPPES snapshot,
    # Luhn-valid by construction). The serializer must not transform it.
    # Hand-crafted packets use npi_from_base — covered above. This
    # exercise hits the surface where a future refactor might
    # accidentally normalize or zero-pad the value.
    specs = all_specs()
    programmatic = [s for s in specs if s.id.startswith("packet-006")
                    or s.id.startswith("packet-007")
                    or s.id.startswith("packet-008")]
    assert programmatic, "expected at least one programmatic packet"
    for s in programmatic:
        identity = golden_for(s)["identity"]
        assert identity["npi"] == s.identity_fields.npi  # type: ignore[union-attr]
