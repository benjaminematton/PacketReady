"""
Locks the determinism contract on `nppes_sampling.sample_n` and `faker_for`.
If either drifts, packet generation stops being reproducible across runs and
the regression gate's baseline.json becomes meaningless.
"""

from __future__ import annotations

import pytest

from packetready_eval.nppes_sampling import (
    DEFAULT_NPPES_CSV,
    SampledProfile,
    csv_file_hash,
    faker_for,
    sample_n,
)


def test_sample_n_shape() -> None:
    out = sample_n(seed=42, n=5)
    assert len(out) == 5
    for p in out:
        assert isinstance(p, SampledProfile)
        assert len(p.npi) == 10 and p.npi.isdigit()
        assert p.primary_specialty
        assert p.taxonomy_code
        assert len(p.license_state) == 2 and p.license_state.isupper()
        assert 1980 < p.license_issuance_year < 2030


def test_sample_n_is_deterministic_for_same_inputs() -> None:
    a = sample_n(seed=42, n=20)
    b = sample_n(seed=42, n=20)
    assert a == b


def test_different_seeds_produce_different_samples() -> None:
    a = sample_n(seed=42, n=20)
    b = sample_n(seed=43, n=20)
    assert a != b


def test_sample_n_raises_when_n_exceeds_rows(tmp_path) -> None:
    tiny = tmp_path / "tiny.csv"
    tiny.write_text(
        "npi,primary_specialty,taxonomy_code,license_state,license_issuance_year\n"
        "1234567890,Internal Medicine,207R00000X,NY,2010\n"
    )
    with pytest.raises(ValueError, match="requested 5"):
        sample_n(seed=1, n=5, nppes_csv=tiny)


def test_csv_file_hash_stable_and_content_sensitive(tmp_path) -> None:
    a = tmp_path / "a.csv"
    a.write_text("x")
    h1 = csv_file_hash(a)
    h2 = csv_file_hash(a)
    assert h1 == h2 and len(h1) == 12
    a.write_text("y")
    assert csv_file_hash(a) != h1


def test_faker_for_is_deterministic_across_runs() -> None:
    # Two Fakers from the same seed must produce identical sequences across
    # several different generators (each pulls from the RNG differently).
    fk_a = faker_for(42)
    fk_b = faker_for(42)
    assert fk_a.name() == fk_b.name()
    assert fk_a.street_address() == fk_b.street_address()
    assert fk_a.phone_number() == fk_b.phone_number()


def test_faker_for_different_seeds_differ() -> None:
    assert faker_for(42).name() != faker_for(43).name()


def test_default_nppes_csv_exists() -> None:
    # Catches the "ran from the wrong cwd" footgun and the "csv was deleted"
    # footgun in one assertion.
    assert DEFAULT_NPPES_CSV.exists(), (
        f"Expected NPPES snapshot at {DEFAULT_NPPES_CSV} — "
        f"run `python3 data/build_nppes_sample.py` to regenerate."
    )
