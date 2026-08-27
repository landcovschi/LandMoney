"""Score a predictor against the labelled eval set and print one number.

    python evals/score.py

The number is macro-averaged recall. Accuracy, a per-category table and the
most common confusions are printed beside it for diagnosis; the metric, and
what it does not capture, is written down in `docs/evals.md`.

    python evals/score.py --check

`--check` compares what this run scored against `baseline.json` beside it, which
is what CI runs -- #58. Without it a CI step that merely runs the scorer is green
while the number drifts, because printing a number is all it takes to exit 0.

Exit code 0 means a number was produced. Exit code 1 means one was not -- an
unreadable file, a label outside the vocabulary, or an empty set. A scorer that
prints 0.0% when it could not score anything is worse than one that refuses.
Exit code 2 is `--check` finding a number it did not expect: the run worked, and
the answer moved. Three codes rather than two, because "the scorer is broken" and
"the baseline moved" want different reactions from whoever reads the red step.

Stdlib only, on purpose, and it stayed that way after #39 moved the rules into
`src/categorizer/`. That move is why the `sys.path` line below exists: the
scorer and the service now run the same `predict`, so this number is a statement
about what the API answers rather than about a copy of it. Reaching the package
by path rather than by installing it keeps `python evals/score.py` a command
that needs no `uv`, no virtual environment and no network -- which is the
property that let #25 exist before any of that did.
"""

import argparse
import sys
from collections import Counter
from dataclasses import dataclass
from datetime import date
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Callable, Iterable

import csv
import json

# The categorizer package is not installed -- `evals/` has no dependencies and no
# virtual environment -- so its import root is put on sys.path by hand. One
# folder, named once, here and in test_score.py.
#
# It has to happen before the imports below it, which is why this block sits
# among them instead of at the top with the rest: the `from categorizer...`
# lines are executed in order, and the path has to exist by the time they run.
# A formatter that sorts imports will move them and break this; there is no
# formatter configured for `evals/`, and that is now a reason rather than an
# omission.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src" / "categorizer" / "src"))

from categorizer.categories import CATEGORIES, KNOWN, MIN_ROWS_PER_CATEGORY
from categorizer.rules import RULES, predict as predict_by_rules

# The eval set's columns, in order. Checked exactly rather than by lookup, so a
# renamed or reordered column is an error instead of a silent column of None.
COLUMNS = ("occurred_at", "amount", "currency", "description", "category")

DEFAULT_SET = Path(__file__).parent / "transactions.csv"

# The recorded score, and the one place to change when it is meant to move --
# which is a change to the CSVs or to a rule, made on purpose, and never a
# number copied out of a red CI step to make it green.
BASELINE = Path(__file__).parent / "baseline.json"

# Both numbers are compared as `render` prints them: one decimal place of a
# percentage, which is how they are written down here, in evals/README.md and in
# CLAUDE.md. Comparing the floats instead would fail on a rounding difference
# nobody can see, and comparing anything coarser would let a row's worth of drift
# through -- one row of 53 is 1.9 points.
BASELINE_PLACES = "{:.1%}"

# What `check` insists baseline.json carries. The `note` and `recorded` keys in
# the file are for whoever opens it and are not read here.
REQUIRED_BASELINE_KEYS = ("set", "rows", "accuracy", "macro_recall")

# Mirrors numeric(18,2) on the Postgres column and DecimalScaleAttribute on the
# .NET side. The eval set is meant to be rows the application could have stored.
MAX_SCALE = 2


@dataclass(frozen=True)
class Row:
    """One labelled transaction. The Transaction entity minus its machine fields."""

    line: int  # the CSV line it came from, so an error can name it
    occurred_at: date
    amount: Decimal
    currency: str
    description: str
    category: str


@dataclass(frozen=True)
class CategoryScore:
    name: str
    rows: int
    correct: int

    @property
    def recall(self) -> float:
        return self.correct / self.rows


@dataclass(frozen=True)
class Report:
    total: int
    correct: int
    per_category: tuple[CategoryScore, ...]
    confusions: Counter

    @property
    def accuracy(self) -> float:
        return self.correct / self.total

    @property
    def macro_recall(self) -> float:
        """The unweighted mean of per-category recall -- the number.

        Averaged over the categories *present in the gold set*, not over the
        whole vocabulary. A category nobody spent money in cannot be recalled,
        and averaging a zero in for it would make the score depend on how many
        categories were declared rather than on the answers.
        """
        return sum(c.recall for c in self.per_category) / len(self.per_category)

    @property
    def thin(self) -> tuple[CategoryScore, ...]:
        """Categories whose recall is a coin flip rather than a measurement."""
        return tuple(c for c in self.per_category if c.rows < MIN_ROWS_PER_CATEGORY)


class EvalSetError(Exception):
    """The file could not be turned into rows. Carries every problem, not the first."""

    def __init__(self, path: Path, problems: list[str]):
        self.path = path
        self.problems = problems
        super().__init__(f"{path}: {len(problems)} problem(s)")


def load(path: Path) -> list[Row]:
    """Read and validate the eval set.

    Every problem is collected before anything is raised. Fixing a
    hand-maintained CSV one error per run is how a labelling session turns into
    an afternoon.
    """
    if not path.exists():
        raise EvalSetError(path, ["the file does not exist"])

    problems: list[str] = []
    rows: list[Row] = []

    # newline="" is required by the csv module rather than optional: it does its
    # own line-ending handling, and letting the file object translate first
    # breaks a quoted field containing a newline.
    #
    # utf-8-sig, not utf-8: Excel writes a BOM, and under plain utf-8 it arrives
    # as part of the first header cell, so the header comparison fails between
    # two strings that print identically. utf-8-sig strips one if it is there and
    # is happy when it is not.
    with path.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.reader(handle)
        try:
            header = next(reader)
        except StopIteration:
            raise EvalSetError(path, ["the file is empty -- not even a header"])

        if tuple(header) != COLUMNS:
            problems.append(
                f"header is {tuple(header)}, expected {COLUMNS}"
            )
            raise EvalSetError(path, problems)

        for line, record in enumerate(reader, start=2):
            if not record or all(not cell.strip() for cell in record):
                continue  # a blank line between labelling sessions is not an error
            if len(record) != len(COLUMNS):
                problems.append(f"line {line}: {len(record)} fields, expected {len(COLUMNS)}")
                continue

            occurred_at_text, amount_text, currency, description, category = (
                cell.strip() for cell in record
            )

            parsed_date = _parse_date(occurred_at_text, line, problems)
            parsed_amount = _parse_amount(amount_text, line, problems)

            if len(currency) != 3 or not currency.isalpha() or not currency.isupper():
                problems.append(
                    f"line {line}: currency {currency!r} is not a three-letter ISO 4217 code"
                )
            if not description:
                problems.append(f"line {line}: description is empty")

            if not category:
                problems.append(
                    f"line {line}: no category. An unlabelled row cannot be scored -- "
                    "if this is the holdout set, it is not meant to be scored yet "
                    "(docs/evals.md, section 4)"
                )
            elif category not in KNOWN:
                problems.append(
                    f"line {line}: category {category!r} is not in the vocabulary. "
                    f"Known: {', '.join(CATEGORIES)}"
                )

            if parsed_date is None or parsed_amount is None:
                continue
            rows.append(
                Row(
                    line=line,
                    occurred_at=parsed_date,
                    amount=parsed_amount,
                    currency=currency,
                    description=description,
                    category=category,
                )
            )

    if problems:
        raise EvalSetError(path, problems)
    return rows


def _parse_date(text: str, line: int, problems: list[str]) -> date | None:
    # fromisoformat is the invariant parse by construction: it knows one format
    # and no locale, which is the same property CLAUDE.md's InvariantCulture rule
    # buys on the .NET side.
    try:
        return date.fromisoformat(text)
    except ValueError:
        problems.append(f"line {line}: date {text!r} is not ISO yyyy-mm-dd")
        return None


def _parse_amount(text: str, line: int, problems: list[str]) -> Decimal | None:
    # Decimal, never float. Decimal("1,50") raises rather than guessing, so a
    # comma decimal separator is caught here instead of becoming a wrong number.
    try:
        amount = Decimal(text)
    except InvalidOperation:
        problems.append(f"line {line}: amount {text!r} is not a decimal number")
        return None
    # Decimal("NaN") and Decimal("Infinity") parse without complaint, and NaN
    # then answers False to every comparison -- so it would slip past the check
    # below and blow up on as_tuple().exponent, which is the string "n" rather
    # than an integer for a non-finite value.
    if not amount.is_finite():
        problems.append(f"line {line}: amount {text!r} is not a finite number")
        return None
    if amount <= 0:
        problems.append(f"line {line}: amount {text!r} is not positive")
        return None
    if -amount.as_tuple().exponent > MAX_SCALE:
        problems.append(
            f"line {line}: amount {text!r} has more than {MAX_SCALE} decimal places"
        )
        return None
    return amount


def score(rows: Iterable[Row], predictor: Callable[[str], str]) -> Report:
    """Run the predictor over the rows and reduce the answers to a Report.

    `predictor` is the extension point slice 4 plugs into: the model adapter is
    another `str -> str`, and the day it exists this function is what compares
    the two. Nothing else in here knows about rules.
    """
    rows = list(rows)
    gold_counts: Counter = Counter()
    correct_counts: Counter = Counter()
    confusions: Counter = Counter()
    correct = 0

    for row in rows:
        prediction = predictor(row.description)
        gold_counts[row.category] += 1
        if prediction == row.category:
            correct_counts[row.category] += 1
            correct += 1
        else:
            confusions[(row.category, prediction)] += 1

    per_category = tuple(
        CategoryScore(name=name, rows=gold_counts[name], correct=correct_counts[name])
        # CATEGORIES rather than gold_counts, so the table keeps its declared
        # order; the filter is what keeps absent categories out of the average.
        for name in CATEGORIES
        if gold_counts[name]
    )
    return Report(
        total=len(rows),
        correct=correct,
        per_category=per_category,
        confusions=confusions,
    )


def render(report: Report, path: Path) -> str:
    width = max(len(c.name) for c in report.per_category)
    lines = [
        f"Eval set : {path}",
        f"Predictor: substring rules ({len(RULES)} of them), evals/rules.py",
        f"Rows     : {report.total} across {len(report.per_category)} categories",
        "",
        f"{'category'.ljust(width)}  rows  correct  recall",
        "-" * (width + 24),
    ]
    for c in report.per_category:
        lines.append(
            f"{c.name.ljust(width)}  {c.rows:>4}  {c.correct:>7}  {c.recall:>5.1%}"
        )
    lines.append("-" * (width + 24))
    lines.append("")

    if report.confusions:
        lines.append("Most common misses (true -> predicted):")
        for (gold, prediction), count in report.confusions.most_common(5):
            lines.append(f"  {gold.ljust(width)} -> {prediction.ljust(width)} {count:>3}")
        lines.append("")

    if report.thin:
        thin = ", ".join(f"{c.name} ({c.rows})" for c in report.thin)
        lines.append(
            f"Thin categories, under {MIN_ROWS_PER_CATEGORY} rows, so their recall is "
            f"nearer a coin flip than a measurement: {thin}"
        )
        lines.append("")

    lines.append(f"accuracy      {report.accuracy:>6.1%}   ({report.correct} of {report.total})")
    lines.append(f"MACRO RECALL  {report.macro_recall:>6.1%}   <-- the number")
    return "\n".join(lines)


def check(report: Report, path: Path, baseline_path: Path = BASELINE) -> int:
    """Compare a report against the recorded baseline. 0 when it reproduces it, 2 when not.

    This is the half of #58 that the CI step could not have on its own: a step
    that only runs the scorer passes while the answer moves, because producing a
    number is the whole of what exit code 0 promises.

    It compares the row count as well as the two percentages, so a CSV that
    gained rows is reported as an eval set that changed rather than as a rule
    that broke. Both are legitimate reasons for the number to move; they are
    just not the same reason, and the failure message is where the difference
    has to be visible.
    """
    try:
        recorded = json.loads(baseline_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        print(f"Cannot read the baseline at {baseline_path}: {error}", file=sys.stderr)
        return 1

    missing = [key for key in REQUIRED_BASELINE_KEYS if key not in recorded]
    if missing:
        print(
            f"{baseline_path} is missing {', '.join(missing)}.",
            file=sys.stderr,
        )
        return 1

    if recorded["set"] != path.name:
        # Only the default set has a recorded number. --check against the
        # holdout, or against a set someone is drafting, would otherwise compare
        # one file's answer with another file's expectation and call it drift.
        print(
            f"The baseline was recorded against {recorded['set']!r}, and this run "
            f"scored {path.name!r}. There is nothing to compare.",
            file=sys.stderr,
        )
        return 1

    problems: list[str] = []
    if recorded["rows"] != report.total:
        problems.append(f"rows: {report.total}, recorded {recorded['rows']}")
    for key, actual in (("accuracy", report.accuracy), ("macro_recall", report.macro_recall)):
        if BASELINE_PLACES.format(actual) != BASELINE_PLACES.format(recorded[key]):
            problems.append(
                f"{key}: {BASELINE_PLACES.format(actual)}, "
                f"recorded {BASELINE_PLACES.format(recorded[key])}"
            )

    if not problems:
        return 0

    print(
        f"The score does not reproduce {baseline_path.name}:",
        *(f"  - {problem}" for problem in problems),
        "",
        "A rule, the vocabulary or a CSV changed. If that was the point, update",
        "the number in the same change -- it is asserted in exactly one place:",
        f"  {baseline_path}",
        "and evals/README.md says what makes moving it legitimate. If it was not",
        "the point, this is the drift the check exists to catch.",
        sep="\n",
        file=sys.stderr,
    )
    return 2


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--check",
        action="store_true",
        help="also compare the score against baseline.json, and exit 2 if it moved",
    )
    parser.add_argument(
        "--set",
        dest="path",
        type=Path,
        default=DEFAULT_SET,
        help=f"the labelled CSV to score (default: {DEFAULT_SET.name})",
    )
    args = parser.parse_args(argv)

    try:
        rows = load(args.path)
    except EvalSetError as error:
        print(f"Cannot score {error.path}:", file=sys.stderr)
        for problem in error.problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    if not rows:
        print(
            f"{args.path} has a header and no rows, so there is nothing to score.\n"
            "The eval set is 30-50 transactions labelled by hand, from real spending.\n"
            "See evals/README.md for how, and docs/evals.md for the vocabulary.",
            file=sys.stderr,
        )
        return 1

    report = score(rows, predict_by_rules)
    # Printed before the comparison either way: a red step whose output is only
    # "the number moved" sends whoever reads it back to run the scorer by hand,
    # and the per-category table is the thing that says which rule did it.
    print(render(report, args.path))
    if args.check:
        return check(report, args.path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
