"""Score a predictor against the labelled eval set and print one number.

    python evals/score.py

The number is macro-averaged recall. Accuracy, the abstention rate, a
per-category table and the most common confusions are printed beside it for
diagnosis; the metric, and what it does not capture, is written down in
`docs/evals.md`. The abstention rate is there because point 3 of that list says
the metric cannot see it: a system that declines and one that answers
confidently and wrongly score identically, and they are not the same system.

    uv run --project src/categorizer python evals/score.py --predictor model

`--predictor` is #60: the same rows, the same day, the same code, scored by the
rules and by the model, so the two numbers are comparable. **It costs money and
needs a key** -- one API call per row, and nothing here caches. It is also the
one command in this folder that needs more than the standard library, which is
why it borrows the categorizer's environment; `--predictor rules` is the default
and still needs no `uv`, no virtual environment and no network.

    python evals/score.py --confusion --misses

`--confusion` prints the full matrix and `--misses` prints every row that was
missed. Both are for reading a result rather than for producing one: the single
number cannot tell a near-miss from a wild one, and #60 asks for the failures to
be reported separately from the percentage.

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
import hashlib
import logging
import os
import sys
from collections import Counter
from dataclasses import dataclass
from datetime import date
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Callable, Iterable, Mapping

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

from categorizer.categories import (
    CATEGORIES,
    KNOWN,
    MIN_ROWS_PER_CATEGORY,
    NO_PREDICTION,
)
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
#
# `predictor` joined them in #60 and is the one that is not a number. Once there
# are two predictors, a recorded score belongs to one of them, and a check that
# did not say which would compare a model run against the rules baseline and call
# the difference drift -- reporting the thing #60 exists to measure as a failure.
REQUIRED_BASELINE_KEYS = ("set", "predictor", "rows", "accuracy", "macro_recall")

# The implementations `--predictor` will build. `rules` is the default and the
# only one that is free; `model` costs an API call per row.
#
# Deliberately not read from anywhere: `main.py`'s CATEGORIZER_PREDICTOR names the
# same two things for the *service*, and the two switches are separate on purpose.
# That one decides what a deployment serves; this one decides what a measurement
# measures, and a scorer that silently took its predictor from the ambient
# environment would produce a number whose meaning depended on a variable nobody
# passed on the command line.
PREDICTORS = ("rules", "model")

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
class Miss:
    """One row the predictor got wrong, kept so `--misses` can name it.

    The row itself rather than a copy of its fields: a miss is only useful with
    the description beside it, and #60's `other` question -- does the model beat
    a baseline that structurally cannot score that category at all -- is answered
    by reading rows, not by reading a percentage.
    """

    row: Row
    predicted: str

    @property
    def abstained(self) -> bool:
        return self.predicted == NO_PREDICTION


@dataclass(frozen=True)
class Report:
    total: int
    correct: int
    per_category: tuple[CategoryScore, ...]
    confusions: Counter
    # Defaulted, so every existing construction of a Report keeps working -- the
    # hand-built ones in test_score.py are about the metric and have no rows to
    # miss. Nothing derived from it is used by `check`, which is what keeps the
    # number this file asserts independent of the diagnosis printed above it.
    misses: tuple[Miss, ...] = ()

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

    @property
    def abstentions(self) -> int:
        """Rows the predictor declined -- #60, reported separately from the metric.

        Derived from `confusions` rather than counted into a field of its own,
        and that is exact rather than approximate: NO_PREDICTION is deliberately
        outside the vocabulary (`categories.py`), every gold label is inside it
        (`load` refuses anything else), so an abstention can never be a correct
        answer and therefore always lands in `confusions`. A separate counter
        would be a second thing that has to agree with the first.
        """
        return sum(
            count
            for (_, prediction), count in self.confusions.items()
            if prediction == NO_PREDICTION
        )

    @property
    def abstention_rate(self) -> float:
        return self.abstentions / self.total

    @property
    def confident_errors(self) -> int:
        """The misses that were an answer rather than a refusal.

        The distinction docs/evals.md, point 3, says the metric cannot make: the
        rules miss 23 rows and 22 of them are abstentions, so their failure is
        one of coverage. A model that misses the same number of rows by answering
        wrongly is a different system with the same score, and it is the one that
        writes a wrong category into the application's column.
        """
        return self.total - self.correct - self.abstentions


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


def score(rows: Iterable[Row], predictor: Callable[[Row], str]) -> Report:
    """Run the predictor over the rows and reduce the answers to a Report.

    `predictor` is the extension point slice 4 plugs into, and #60 is the day it
    was plugged into. It takes the **whole row** rather than the description,
    which is a widening of the seam this file used to describe as `str -> str`
    and is the one design decision in that issue worth arguing about.

    What forced it: the model is shown the amount and the currency -- `prompt.py`
    says so in as many words, because a 4.50 and a 450 at the same merchant are
    not the same purchase -- so a scorer that could only hand over a description
    would be measuring a different predictor from the one the service runs. That
    is exactly the drift #39 moved `rules.py` out of this folder to prevent, and
    it would be undetectable: the number would simply be lower.

    What it costs, and why it is safe: the rules read nothing but the description,
    so wrapping them changes no answer. That is not an argument, it is what
    `--check` asserts on every pull request -- the recorded 56.1% reproduced
    across this change, or the change is wrong.

    Nothing in here knows about rules, or about models.
    """
    rows = list(rows)
    gold_counts: Counter = Counter()
    correct_counts: Counter = Counter()
    confusions: Counter = Counter()
    misses: list[Miss] = []
    correct = 0

    for row in rows:
        prediction = predictor(row)
        gold_counts[row.category] += 1
        if prediction == row.category:
            correct_counts[row.category] += 1
            correct += 1
        else:
            confusions[(row.category, prediction)] += 1
            misses.append(Miss(row=row, predicted=prediction))

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
        misses=tuple(misses),
    )


def render(report: Report, path: Path, predictor: str) -> str:
    width = max(len(c.name) for c in report.per_category)
    lines = [
        f"Eval set : {path}",
        # Passed in rather than written here, and the line it replaced said
        # "evals/rules.py" -- a path that has not existed since #39 moved the
        # rules into the categorizer. A header describing the predictor has to
        # come from whatever built the predictor, or it describes the one that
        # was there when the string was typed.
        f"Predictor: {predictor}",
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
    # Printed below the number and never folded into it. An abstention and a
    # confident error cost the same point of macro recall and cost the owner
    # very different amounts of attention -- docs/evals.md, point 3 -- so this
    # is the line that says which kind of system produced the percentage above.
    confident = report.confident_errors
    lines.append(
        f"abstained     {report.abstention_rate:>6.1%}   "
        f"({report.abstentions} of {report.total}); the other "
        f"{confident} {'miss was a confident answer' if confident == 1 else 'misses were confident answers'}"
    )
    return "\n".join(lines)


def render_confusion(report: Report) -> str:
    """The full matrix: rows are the true category, columns what was predicted.

    Every category in the vocabulary gets a column whether or not anything landed
    in it, plus one for the abstention -- so the shape is the same for both
    predictors and the two can be read side by side. A matrix whose columns
    depended on the answers would be a different table each run.

    Zeroes print as `.` because the diagonal is the thing being looked for, and a
    grid of aligned zeroes hides it.
    """
    width = max(len(c.name) for c in report.per_category)
    columns = (*CATEGORIES, NO_PREDICTION)
    correct = {c.name: c.correct for c in report.per_category}

    lines = [
        "Confusion matrix. Row = true category, column = predicted; the diagonal",
        f"is correct and `{NO_PREDICTION[:3]}` is an abstention. Columns are the first",
        "three letters of each category, in the order the table above uses.",
        "",
        " " * width + "  " + " ".join(name[:3].rjust(4) for name in columns),
    ]
    for c in report.per_category:
        cells = []
        for name in columns:
            count = correct[c.name] if name == c.name else report.confusions[(c.name, name)]
            cells.append(str(count).rjust(4) if count else ".".rjust(4))
        lines.append(c.name.ljust(width) + "  " + " ".join(cells))
    return "\n".join(lines)


def render_misses(report: Report) -> str:
    """Every missed row, so a number can be read as rows rather than believed.

    Ordered by true category rather than by line, because the question this
    answers is "what does it not understand" and not "where in the file". The
    line number is carried anyway: a row worth arguing about has to be findable.
    """
    if not report.misses:
        return "No misses."
    width = max(len(m.row.category) for m in report.misses)
    lines = [f"The {len(report.misses)} missed rows (true -> predicted):"]
    for miss in sorted(report.misses, key=lambda m: (m.row.category, m.row.line)):
        lines.append(
            f"  line {miss.row.line:>3}  {miss.row.category.ljust(width)} -> "
            f"{miss.predicted.ljust(width)}  {miss.row.description}"
        )
    return "\n".join(lines)


def check(
    report: Report,
    path: Path,
    baseline_path: Path = BASELINE,
    predictor: str = PREDICTORS[0],
) -> int:
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

    if recorded["predictor"] != predictor:
        # The guard #60 needs, and it is the same argument as the one below it
        # one level up: a recorded number belongs to a set *and* to whatever
        # produced the answers. Without this, `--predictor model --check` would
        # compare the model against the rules baseline and exit 2 -- reporting
        # the improvement the whole slice exists to produce as drift, in a
        # message telling whoever read it to update the baseline to match.
        print(
            f"The baseline was recorded for the {recorded['predictor']!r} predictor, "
            f"and this run used {predictor!r}. There is nothing to compare.",
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


class ModelCallFailed(logging.Handler):
    """Counts the adapter's ERROR records, so a broken run cannot print a number.

    This exists because of the one property of `AnthropicPredictor` that is right
    for the service and wrong for a scorer: it never raises. Every failure -- no
    credential, a 401, a timeout, a bug in the adapter -- becomes a null category,
    which is what keeps a user's transaction safe on the .NET side, and which here
    is **indistinguishable from the model declining the row**. A run where forty
    calls failed would score about 20% and read as a model that is bad at the job.

    The split is the adapter's own levels rather than anything invented here:
    `logger.exception` (ERROR) is the call failing, `logger.warning` is the model
    answering something unusable -- which is a genuine miss and must stay in the
    number. So this handler counts ERROR and `main` refuses to print a score if
    the count is not zero. Same principle as refusing an empty set: a scorer that
    reports 0.0% when it could not score is worse than one that stops.
    """

    def __init__(self) -> None:
        super().__init__(level=logging.ERROR)
        self.count = 0

    def emit(self, record: logging.LogRecord) -> None:
        self.count += 1


def build_predictor(
    name: str, env: Mapping[str, str], total: int
) -> tuple[Callable[[Row], str], str]:
    """The predictor and the label that says what it was, for the header.

    Returns both together on purpose: #60 asks for the number and the thing that
    produced it, and a header built anywhere other than beside the construction
    can describe a predictor that was not run.

    `total` is only for the progress counter, and taking it means `main` has to
    load the CSV before building this -- which is the ordering that matters when
    the predictor costs money: a malformed eval set fails before the first API
    call rather than after the fifty-third.
    """
    if name == "rules":
        return (
            lambda row: predict_by_rules(row.description),
            f"substring rules ({len(RULES)} of them), "
            "src/categorizer/src/categorizer/rules.py",
        )

    # Imported here rather than at the top, and it is the line that keeps this
    # folder's one promise. `evals/` is stdlib-only -- no uv, no virtual
    # environment, no network -- and the model path needs the `anthropic` package,
    # which lives in the categorizer's project. A top-level import would make
    # `python evals/score.py` fail on a fresh clone for the sake of a code path it
    # was not asked to run, and CI runs the rules path on the runner's own python
    # precisely so that an accidental dependency in here is caught.
    from categorizer.anthropic_predictor import AnthropicPredictor
    from categorizer.contracts import CategorizeRequest
    from categorizer.prompt import SYSTEM_PROMPT

    predictor = AnthropicPredictor.from_env(env)
    done = 0

    def predict(row: Row) -> str:
        nonlocal done
        done += 1
        # stderr, so `python evals/score.py --predictor model > result.txt` keeps
        # the report clean while a run that takes minutes still says it is alive.
        print(f"  [{done}/{total}] {row.description}", file=sys.stderr, flush=True)

        answer = predictor.categorize(
            CategorizeRequest(
                description=row.description,
                amount=row.amount,
                currency=row.currency,
            )
        )
        # The sentinel goes back in. `CategorizeResponse` carries None because
        # `unknown` must never cross the HTTP boundary into the .NET column (#39);
        # the metric needs the opposite -- a value that is not a category and is
        # therefore always a miss. Both halves of that decision are honoured by
        # translating here rather than by either side changing its mind.
        return NO_PREDICTION if answer.category is None else answer.category.value

    # The prompt is hashed rather than printed: it is 40 lines and the report is
    # read at a glance. What the digest buys is the half of #60 that is not a
    # percentage -- "a score without the prompt beside it is not reproducible" --
    # because an edited prompt changes this string, so a recorded number and a
    # later run visibly disagree about what was measured rather than silently
    # agreeing. The prompt itself is in git, one file, `prompt.py`.
    fingerprint = hashlib.sha256(SYSTEM_PROMPT.encode("utf-8")).hexdigest()[:12]
    return (
        predict,
        f"{predictor.model}, effort={predictor.effort}, "
        f"prompt.py sha256:{fingerprint}",
    )


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
    parser.add_argument(
        "--predictor",
        choices=PREDICTORS,
        default=PREDICTORS[0],
        help="what answers the rows. 'model' costs one API call per row (default: rules)",
    )
    parser.add_argument(
        "--confusion",
        action="store_true",
        help="also print the full confusion matrix",
    )
    parser.add_argument(
        "--misses",
        action="store_true",
        help="also print every missed row with its description",
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

    failures = ModelCallFailed()
    if args.predictor == "model":
        # Only on the model path. The rules cannot fail, and turning logging on
        # for them would print nothing while making a stdlib-only script look
        # like it has an opinion about log configuration.
        logging.basicConfig(level=logging.INFO, stream=sys.stderr)
        logging.getLogger("categorizer").addHandler(failures)

    try:
        predictor, label = build_predictor(args.predictor, os.environ, len(rows))
    except ImportError as error:
        print(
            f"--predictor {args.predictor} needs the categorizer's dependencies: {error}",
            "Run it through the categorizer's environment:",
            "  uv run --project src/categorizer python evals/score.py --predictor model",
            sep="\n",
            file=sys.stderr,
        )
        return 1

    if failures.count:
        # Before a single row is scored, and it is worth the extra branch: the
        # one thing the adapter logs at construction is a missing credential, and
        # without this the run would make 53 doomed calls first -- free against no
        # key at all, but 53 real round trips against a wrong one.
        print(
            "The predictor reported an error before any row was scored, so nothing",
            "was run. The line above says what; a missing ANTHROPIC_API_KEY is the",
            "usual one, and it is never read from this repository.",
            sep="\n",
            file=sys.stderr,
        )
        return 1

    report = score(rows, predictor)

    if failures.count:
        # Deliberately before anything is printed. A number produced by a run in
        # which calls failed is not a low score, it is not a score -- and the
        # cheapest way for it to end up in `docs/evals.md` anyway is for it to
        # have been printed next to a warning.
        print(
            f"{failures.count} of {len(rows)} calls failed, so there is no number.",
            "The tracebacks are above. Every failure of the model is a null category",
            "by design, which is right for the service and useless here: it is",
            "indistinguishable from the model declining the row.",
            sep="\n",
            file=sys.stderr,
        )
        return 1

    # Printed before the comparison either way: a red step whose output is only
    # "the number moved" sends whoever reads it back to run the scorer by hand,
    # and the per-category table is the thing that says which rule did it.
    print(render(report, args.path, label))
    if args.confusion:
        print()
        print(render_confusion(report))
    if args.misses:
        print()
        print(render_misses(report))
    if args.check:
        return check(report, args.path, BASELINE, args.predictor)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
