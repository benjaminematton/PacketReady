"""
Per-document name shape vocabulary for the P4 eval set + tuning subset.

Two enumerations, both consumed by :mod:`packetready_eval.packets` (clean
patterns when building a fresh spec) and :mod:`packetready_eval.conflict_planters`
(conflict shapes when mutating a clean spec):

- :class:`CleanNamePattern` — how the SAME person's name appears across
  license / DEA / board cert / malpractice on a packet that should NOT
  trigger the IdentityCoherence validator. Five patterns cover the
  normalization cases the LLM has to learn to ignore: credential suffix
  (MD vs M.D. vs bare), middle initial inclusion/punctuation, hyphenated
  surnames that are consistent across docs, and whitespace differences.

- :class:`ConflictShape` — how malpractice's fullName diverges from the
  other three docs on a packet that SHOULD trigger IdentityCoherence
  (with one exception: :pyattr:`ConflictShape.SURNAME_TYPO` plants a
  near-disagreement that the validator must NOT flag, recorded as
  ``expected_to_flag=False`` on the marker).

Keeping both enumerations + their template functions in one module means
:mod:`packets` and :mod:`conflict_planters` can't drift on which patterns
exist or how they're rendered. Adding a sixth pattern is one enum entry +
one template function + one test row.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum, auto
from random import Random

# ---------------------------------------------------------------------------
# Clean patterns: name SHOULD appear consistent across docs
# ---------------------------------------------------------------------------


class CleanNamePattern(Enum):
    """How the same person's name appears across the four document PDFs.

    All patterns are normalization equivalents — they MUST NOT trigger the
    IdentityCoherence validator. Each value rotates into the 50-packet eval
    set via packet_idx so the tuning subset and the held-out set both cover
    every pattern.
    """

    # "Henry Anderson, MD" on license/board/malpractice; "Henry Anderson" on DEA.
    CREDENTIAL_MD = auto()
    # "Henry Anderson, M.D." on license/board/malpractice; bare on DEA.
    # Periods-in-credential is its own normalization case — strip both forms.
    CREDENTIAL_PERIODS = auto()
    # "Henry J. Anderson, MD" on license/board/malpractice;
    # "Henry J Anderson" on DEA (no period, no credential).
    MIDDLE_INITIAL = auto()
    # "Henry Anderson-Jones, MD" on every doc — the hyphen-already case.
    # If the LLM flags hyphenation as a conflict, it'll fire here too.
    HYPHENATED_ALREADY = auto()
    # "Henry Anderson, MD" on three docs; "Henry  Anderson, MD" (double
    # space) on board cert — the whitespace-collapse case.
    WHITESPACE_VARIANT = auto()


@dataclass(frozen=True)
class DocNames:
    """Per-doc fullName values for one clean pattern."""
    license: str
    dea: str
    board_cert: str
    malpractice: str


def names_for_clean_pattern(
    pattern: CleanNamePattern,
    first: str,
    last: str,
) -> DocNames:
    """The four per-doc fullName values for `first`/`last` under `pattern`.

    Used by :pyfunc:`packetready_eval.packets._spec_from_profile` to populate
    LicenseFields/DeaFields/BoardCertFields/MalpracticeFields. The reverse
    direction (parsing a per-doc name back into first/last) is the planter's
    job — that's why the planters take `(first, last)` rather than re-parsing.
    """
    if pattern is CleanNamePattern.CREDENTIAL_MD:
        with_md = f"{first} {last}, MD"
        bare = f"{first} {last}"
        return DocNames(license=with_md, dea=bare, board_cert=with_md, malpractice=with_md)
    if pattern is CleanNamePattern.CREDENTIAL_PERIODS:
        with_md = f"{first} {last}, M.D."
        bare = f"{first} {last}"
        return DocNames(license=with_md, dea=bare, board_cert=with_md, malpractice=with_md)
    if pattern is CleanNamePattern.MIDDLE_INITIAL:
        # Fixed middle initial J for determinism; the LLM only cares about
        # the shape (initial present/absent, period present/absent), not the letter.
        with_initial_dotted = f"{first} J. {last}, MD"
        dea_no_dot = f"{first} J {last}"
        return DocNames(
            license=with_initial_dotted,
            dea=dea_no_dot,
            board_cert=with_initial_dotted,
            malpractice=with_initial_dotted,
        )
    if pattern is CleanNamePattern.HYPHENATED_ALREADY:
        # Pre-existing hyphenation on ALL docs. The LLM must not learn
        # "hyphen-on-malpractice = conflict"; this is the negative example.
        full = f"{first} {last}-Jones, MD"
        dea = f"{first} {last}-Jones"
        return DocNames(license=full, dea=dea, board_cert=full, malpractice=full)
    if pattern is CleanNamePattern.WHITESPACE_VARIANT:
        normal = f"{first} {last}, MD"
        doubled = f"{first}  {last}, MD"  # two spaces between first and last
        bare = f"{first} {last}"
        return DocNames(license=normal, dea=bare, board_cert=doubled, malpractice=normal)
    raise ValueError(f"Unhandled CleanNamePattern: {pattern}")


# ---------------------------------------------------------------------------
# Conflict shapes: malpractice diverges from the other three docs
# ---------------------------------------------------------------------------


class ConflictShape(Enum):
    """How malpractice's fullName diverges from license/dea/board on a planted packet.

    All shapes except :pyattr:`SURNAME_TYPO` are real disagreements the
    validator SHOULD flag. SURNAME_TYPO is a one-character surname swap
    (e.g. "Anderson" → "Andersan") — by P4's FP-discipline rules, that's a
    near-disagreement the validator must NOT flag (typo tolerance). The
    marker records ``expected_to_flag=False`` for this shape so the metrics
    counter scores it correctly.
    """

    HYPHENATED_SUFFIX = auto()   # current behavior: "Anderson" → "A. Anderson-Smith"
    MIDDLE_NAME_ADDED = auto()   # "Henry Anderson" → "Henry James Anderson"
    NICKNAME = auto()            # "Robert Anderson" → "Bob Anderson"
    SURNAME_TYPO = auto()        # "Anderson" → "Andersan" — DO NOT FLAG
    SURNAME_SWAP = auto()        # "Anderson" → "Bautista" — always flag


EXPECTED_TO_FLAG: dict[ConflictShape, bool] = {
    ConflictShape.HYPHENATED_SUFFIX: True,
    ConflictShape.MIDDLE_NAME_ADDED: True,
    ConflictShape.NICKNAME: True,
    ConflictShape.SURNAME_TYPO: False,   # the inversion
    ConflictShape.SURNAME_SWAP: True,
}
assert set(EXPECTED_TO_FLAG) == set(ConflictShape), \
    "EXPECTED_TO_FLAG must list every ConflictShape"


# Hyphenated surname suffixes for HYPHENATED_SUFFIX shape.
_HYPHENATED_SURNAME_SUFFIXES: tuple[str, ...] = (
    "Smith", "Johnson", "Williams", "Brown", "Davis",
    "Miller", "Wilson", "Moore", "Taylor", "Harris",
)

# Plausible middle names for MIDDLE_NAME_ADDED.
_MIDDLE_NAMES: tuple[str, ...] = (
    "James", "Marie", "Alexander", "Elizabeth", "Michael",
    "Catherine", "Joseph", "Rose", "Daniel", "Anne",
)

# (formal, nickname) pairs for NICKNAME. The planter swaps formal↔nickname
# on the malpractice doc. If `first` doesn't match any formal, the planter
# falls back to NAME truncation ("Christopher" → "Chris" by initials).
_NICKNAMES: tuple[tuple[str, str], ...] = (
    ("Robert", "Bob"), ("Margaret", "Maggie"), ("William", "Bill"),
    ("Elizabeth", "Liz"), ("Richard", "Rick"), ("Michael", "Mike"),
    ("Jennifer", "Jenny"), ("Christopher", "Chris"), ("Patricia", "Pat"),
    ("Anthony", "Tony"), ("Jonathan", "Jon"), ("Katherine", "Kate"),
    ("Nicholas", "Nick"), ("Samantha", "Sam"), ("Stephen", "Steve"),
)
_FORMAL_TO_NICK: dict[str, str] = {f: n for f, n in _NICKNAMES}

# Surname-swap pool — totally unrelated surnames so the disagreement is
# unambiguous. Curated to avoid accidental hyphen/initial overlap with the
# `_HYPHENATED_SURNAME_SUFFIXES` pool.
_SWAP_SURNAMES: tuple[str, ...] = (
    "Hernandez", "Goldberg", "Nakamura", "Patel", "Okonkwo",
    "Bautista", "Lindqvist", "Voss", "Rasmussen", "Cheng",
)


def malpractice_variant_for_shape(
    shape: ConflictShape,
    first: str,
    last: str,
    rng: Random,
    *,
    license_suffix: str = ", MD",
) -> str:
    """Returns the malpractice fullName for `shape` given the (first, last)
    that appear on license/dea/board.

    `license_suffix` is the credential suffix attached on the clean pattern
    (", MD" for CREDENTIAL_MD, ", M.D." for CREDENTIAL_PERIODS, etc.) — the
    variant carries the SAME suffix so the disagreement is only on the
    name itself, not also on the credential.
    """
    if shape is ConflictShape.HYPHENATED_SUFFIX:
        candidates = tuple(s for s in _HYPHENATED_SURNAME_SUFFIXES if s != last)
        suffix = rng.choice(candidates)
        return f"{first} {first[0]}. {last}-{suffix}{license_suffix}"

    if shape is ConflictShape.MIDDLE_NAME_ADDED:
        middle = rng.choice(_MIDDLE_NAMES)
        return f"{first} {middle} {last}{license_suffix}"

    if shape is ConflictShape.NICKNAME:
        # If `first` is a known formal name, swap to its nickname. Otherwise
        # fall back to deterministic-from-rng pair (preserves coverage at
        # the cost of an arbitrary first-name swap).
        nick = _FORMAL_TO_NICK.get(first)
        if nick is None:
            # No formal-name match; pick any formal/nickname pair and use the
            # nickname with the original surname. Still a real first-name
            # disagreement worth flagging.
            _, nick = rng.choice(_NICKNAMES)
        return f"{nick} {last}{license_suffix}"

    if shape is ConflictShape.SURNAME_TYPO:
        if len(last) < 3:
            # Can't safely typo a two-letter surname — swap last two letters
            # would still produce a "real" disagreement. Pin to a fixed typo.
            typo = last + "x"
        else:
            # Replace the penultimate letter with its neighbor in the alphabet.
            # Deterministic per (last, rng) and shape-preserving (length stays
            # the same, only one letter differs).
            idx = len(last) - 2
            ch = last[idx]
            shifted = chr(((ord(ch.lower()) - ord('a') + 1) % 26) + ord('a'))
            if ch.isupper():
                shifted = shifted.upper()
            typo = last[:idx] + shifted + last[idx + 1:]
        return f"{first} {typo}{license_suffix}"

    if shape is ConflictShape.SURNAME_SWAP:
        candidates = tuple(s for s in _SWAP_SURNAMES if s != last)
        new_surname = rng.choice(candidates)
        return f"{first} {new_surname}{license_suffix}"

    raise ValueError(f"Unhandled ConflictShape: {shape}")
