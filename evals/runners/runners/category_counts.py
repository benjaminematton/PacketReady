"""
Iteration-trend aggregator for the IdentityCoherence tuning loop.

Reads every ``evals/tuning-runs/iter-*__failures.tsv`` file in order and
prints a per-category trend table with ``Δ-vs-baseline``. The category
column on each TSV is filled in by hand between iterations (Phase 1 of the
loop in docs/impl/phase-4 — categorize failures before deciding what to
change).

The Δ-vs-baseline column is the load-bearing signal — it catches the
regression mode where iteration N targeted category A successfully but
silently re-introduced category B that an earlier iteration had fixed.
Reactive per-failure iteration hides this; the running total surfaces it.

Output shape::

    category                  iter-0  iter-1  iter-2  iter-3  Δ-vs-baseline
    credential_suffix           3       2       0       0       -3
    middle_initial              1       1       1       1        0
    surname_typo_overreact      0       0       2       2       +2  ← regression
    TOTAL                       4       3       3       3       -1

If no baseline.json exists yet, the Δ column is omitted (and the script
prints a hint to run the baseline first).

Usage::

    python3 evals/runners/runners/category_counts.py
    python3 evals/runners/runners/category_counts.py --runs-dir custom/dir
"""

from __future__ import annotations

import argparse
import csv
import re
import sys
from collections import Counter, OrderedDict
from pathlib import Path


_ITER_TSV_PATTERN = re.compile(r"^iter-(\d+)__failures\.tsv$")


def _read_categorized_failures(tsv_path: Path) -> Counter[str]:
    """Returns counter of categories from one iteration's TSV. Rows with an
    empty `category` column are reported as `"uncategorized"` so the human
    sees them in the trend table and is nudged to fill them in."""
    counts: Counter[str] = Counter()
    with tsv_path.open() as f:
        reader = csv.DictReader(f, delimiter="\t")
        for row in reader:
            cat = (row.get("category") or "").strip()
            counts[cat or "uncategorized"] += 1
    return counts


def _read_baseline_categories(runs_dir: Path) -> Counter[str] | None:
    """The baseline run's failures TSV is `iter-00__failures.tsv`. Returns
    None when the baseline hasn't been recorded yet."""
    baseline_tsv = runs_dir / "iter-00__failures.tsv"
    if not baseline_tsv.exists():
        return None
    return _read_categorized_failures(baseline_tsv)


def _iteration_tsvs(runs_dir: Path) -> list[tuple[int, Path]]:
    """Sorted (iteration_index, path) for every iter-NN__failures.tsv."""
    out: list[tuple[int, Path]] = []
    for p in runs_dir.iterdir():
        m = _ITER_TSV_PATTERN.match(p.name)
        if m:
            out.append((int(m.group(1)), p))
    out.sort()
    return out


def _print_table(
    iters: list[tuple[int, Counter[str]]],
    baseline: Counter[str] | None,
) -> None:
    if not iters:
        print("No iteration TSVs found. Run the CLI with --baseline --iteration 0 first.")
        return

    all_categories = sorted({c for _, counts in iters for c in counts})
    if not all_categories:
        print("No failures recorded across any iteration. Either everything is passing")
        print("or the failures TSVs are empty — check the latest CLI output.")
        return

    iter_labels = [f"iter-{i:02d}" for i, _ in iters]
    header = ["category", *iter_labels]
    if baseline is not None:
        header.append("Δ-vs-baseline")

    rows: list[list[str]] = []
    latest_counts = iters[-1][1]
    for cat in all_categories:
        row = [cat]
        for _, counts in iters:
            row.append(str(counts.get(cat, 0)))
        if baseline is not None:
            delta = latest_counts.get(cat, 0) - baseline.get(cat, 0)
            sign = "+" if delta > 0 else ""
            marker = "  ← regression" if delta > 0 else ""
            row.append(f"{sign}{delta}{marker}")
        rows.append(row)

    # Total row
    total_row = ["TOTAL"]
    for _, counts in iters:
        total_row.append(str(sum(counts.values())))
    if baseline is not None:
        delta = sum(latest_counts.values()) - sum(baseline.values())
        sign = "+" if delta > 0 else ""
        total_row.append(f"{sign}{delta}")
    rows.append(total_row)

    col_widths = [max(len(r[c]) for r in [header, *rows]) for c in range(len(header))]
    fmt = "  ".join(f"{{:<{w}}}" for w in col_widths)
    print(fmt.format(*header))
    print(fmt.format(*["-" * w for w in col_widths]))
    for r in rows:
        print(fmt.format(*r))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--runs-dir",
        type=Path,
        default=Path(__file__).resolve().parents[3] / "evals" / "tuning-runs",
        help="Directory containing iter-NN__failures.tsv files.",
    )
    args = parser.parse_args(argv)

    if not args.runs_dir.exists():
        print(f"No tuning-runs directory at {args.runs_dir}.", file=sys.stderr)
        return 1

    iters_paths = _iteration_tsvs(args.runs_dir)
    iters = [(i, _read_categorized_failures(p)) for i, p in iters_paths]
    baseline = _read_baseline_categories(args.runs_dir)

    uncategorized_in_latest = (
        iters[-1][1].get("uncategorized", 0) if iters else 0
    )
    if uncategorized_in_latest > 0:
        print(
            f"NOTE: {uncategorized_in_latest} row(s) in the latest TSV have no category. "
            "Fill them in before the next iteration so the trend is meaningful.\n"
        )

    _print_table(iters, baseline)

    if baseline is None and iters:
        print()
        print("(No baseline.json found. Run with --baseline --iteration 0 to lock the floor.)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
