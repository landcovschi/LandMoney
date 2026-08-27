"""Tests for the scorer and the rules.

    python evals/test_score.py

Run as a script rather than through `python -m unittest` from the repository
root, because `score` is imported as a top-level module and it is the script's
own folder that lands on `sys.path`. That is the price of `evals/` having no
package of its own, and it is one line of README. (#39 gave the *rules* a
package and left this half alone on purpose -- see the import block below.)

stdlib `unittest`, not pytest, for the same reason the rest of `evals/` is
stdlib: nothing here needs a dependency, and the categorizer's toolchain can
arrive with the categorizer.

What these are for, in the spirit of #21's mutation sweep: **a test that cannot
fail is decoration.** The important one is `test_always_guessing_the_majority`,
which is the whole argument for macro recall over accuracy, asserted on a
hand-built example rather than believed.
"""

import io
import json
import tempfile
import unittest
from collections import Counter
from contextlib import redirect_stderr
from datetime import date
from decimal import Decimal
from pathlib import Path

# score.py puts src/categorizer/src on sys.path as a side effect of being
# imported, and these two lines rely on that having happened. Deliberately not
# repeated here: two copies of a path is two places to be wrong, and the import
# below fails loudly and immediately if the arrangement ever changes.
from score import (  # noqa: I001 -- must come first
    BASELINE,
    COLUMNS,
    DEFAULT_SET,
    PREDICTORS,
    CategoryScore,
    EvalSetError,
    Report,
    Row,
    build_predictor,
    check,
    load,
    render_confusion,
    render_misses,
    score,
)

from categorizer.categories import CATEGORIES, KNOWN, NO_PREDICTION
from categorizer.rules import RULES, predict


def row(category: str, description: str = "irrelevant") -> Row:
    """A Row whose only interesting field is the label."""
    return Row(
        line=0,
        occurred_at=date(2026, 8, 24),
        amount=Decimal("1.00"),
        currency="EUR",
        description=description,
        category=category,
    )


def always(answer: str):
    """A predictor. Takes the whole Row since #60 -- see `score`'s docstring."""
    return lambda _row: answer


class MetricTests(unittest.TestCase):
    def test_always_guessing_the_majority_scores_40_percent_accuracy_and_9_percent_macro(self):
        """The failure #25 named, and the reason the metric is not accuracy.

        20 rows, 8 of them groceries -- 40% -- and all eleven categories
        present. A system that answers "groceries" to everything has learned
        nothing at all.
        """
        rows = [row("groceries") for _ in range(8)]
        for category in CATEGORIES:
            if category == "groceries":
                continue
            rows.append(row(category))
        # Two categories get a second row, to reach 20 rows and therefore an
        # exact 40% majority.
        rows.append(row("eating-out"))
        rows.append(row("transport"))
        self.assertEqual(len(rows), 20)

        report = score(rows, always("groceries"))

        self.assertAlmostEqual(report.accuracy, 0.40)
        # One category at 100%, ten at 0%, averaged without weighting.
        self.assertAlmostEqual(report.macro_recall, 1 / 11)
        self.assertLess(report.macro_recall, report.accuracy / 4)

    def test_perfect_predictions_score_one_hundred(self):
        # An oracle: the description is the label, and the predictor returns it.
        rows = [row(c, description=c) for c in CATEGORIES]
        report = score(rows, lambda r: r.description)

        self.assertEqual(report.macro_recall, 1.0)
        self.assertEqual(report.accuracy, 1.0)
        self.assertEqual(report.confusions, {})

    def test_absent_categories_are_not_averaged_in(self):
        """Three categories in the set, so the denominator is three, not eleven."""
        rows = [row("groceries"), row("transport"), row("health")]
        report = score(rows, always("groceries"))

        self.assertEqual(len(report.per_category), 3)
        self.assertAlmostEqual(report.macro_recall, 1 / 3)

    def test_an_abstention_is_a_miss_like_any_other(self):
        """NO_PREDICTION gets no discount -- see docs/evals.md, point 3."""
        rows = [row("groceries"), row("transport")]

        abstaining = score(rows, always(NO_PREDICTION))
        confidently_wrong = score(rows, always("fees"))

        self.assertEqual(abstaining.macro_recall, 0.0)
        self.assertEqual(abstaining.macro_recall, confidently_wrong.macro_recall)

    def test_abstaining_cannot_be_right_about_the_other_category(self):
        """The mutation the test above does not kill.

        Point NO_PREDICTION at a real category -- `other` is the tempting one,
        since "no answer" and "fits nothing" sound alike -- and the baseline
        starts scoring `other` rows correctly for the wrong reason. That is the
        fallback docs/evals.md rejected, and it makes the one category whose
        size is a warning sign look healthy. Two assertions, because either one
        alone leaves the door open.
        """
        self.assertNotIn(NO_PREDICTION, KNOWN)

        report = score([row("other")], always(NO_PREDICTION))
        self.assertEqual(report.macro_recall, 0.0)

    def test_thin_categories_are_named(self):
        rows = [row("groceries") for _ in range(5)] + [row("gifts")]
        report = score(rows, always("groceries"))

        self.assertEqual([c.name for c in report.thin], ["gifts"])

    def test_confusions_record_the_direction(self):
        rows = [row("eating-out"), row("eating-out"), row("transport")]
        report = score(rows, always("groceries"))

        self.assertEqual(report.confusions[("eating-out", "groceries")], 2)
        self.assertEqual(report.confusions[("transport", "groceries")], 1)

    def test_the_abstention_rate_counts_refusals_and_not_wrong_answers(self):
        """#60: the distinction the metric cannot make, made beside it.

        Two systems with identical macro recall, one of which declines and one of
        which answers confidently and wrongly. `abstention_rate` is the only thing
        in the report that tells them apart, which is why it is reported at all.
        """
        rows = [row("groceries"), row("transport")]

        abstaining = score(rows, always(NO_PREDICTION))
        confidently_wrong = score(rows, always("fees"))

        self.assertEqual(abstaining.macro_recall, confidently_wrong.macro_recall)
        self.assertEqual(abstaining.abstention_rate, 1.0)
        self.assertEqual(abstaining.confident_errors, 0)
        self.assertEqual(confidently_wrong.abstention_rate, 0.0)
        self.assertEqual(confidently_wrong.confident_errors, 2)

    def test_correct_plus_abstentions_plus_confident_errors_is_every_row(self):
        """The three add up, which is what makes reading two of them enough.

        `abstentions` is derived from the confusion counter rather than counted,
        so this is the assertion that the derivation is exact rather than nearly
        right -- it fails the moment an abstention can be scored as correct.
        """
        rows = [row("groceries"), row("groceries"), row("transport"), row("fees")]
        report = score(
            rows,
            lambda r: {
                "groceries": "groceries",
                "transport": NO_PREDICTION,
                "fees": "housing",
            }[r.category],
        )

        self.assertEqual(report.correct, 2)
        self.assertEqual(report.abstentions, 1)
        self.assertEqual(report.confident_errors, 1)
        self.assertEqual(
            report.correct + report.abstentions + report.confident_errors, report.total
        )

    def test_a_miss_carries_the_row_it_missed(self):
        """Not the count -- the row. #60 asks which rows, and `other` by name."""
        rows = [row("other", description="parcel by post"), row("gifts")]
        report = score(rows, always(NO_PREDICTION))

        self.assertEqual(len(report.misses), 2)
        missed = {m.row.category: m for m in report.misses}
        self.assertEqual(missed["other"].row.description, "parcel by post")
        self.assertTrue(missed["other"].abstained)


class RuleTests(unittest.TestCase):
    def test_every_rule_names_a_category_that_exists(self):
        for needle, category in RULES:
            with self.subTest(rule=needle):
                self.assertIn(category, KNOWN)

    def test_no_rule_is_declared_twice(self):
        needles = [needle for needle, _ in RULES]
        self.assertEqual(len(needles), len(set(needles)))

    def test_nothing_matched_means_no_prediction(self):
        self.assertEqual(predict("Ludicrously specific thing"), NO_PREDICTION)

    def test_matching_ignores_case(self):
        self.assertEqual(predict("PHARMACY"), "health")
        self.assertEqual(predict("Pharmacy"), "health")

    def test_specific_rules_win_over_the_general_ones_they_would_hide(self):
        """The seven ordering collisions named in rules.py.

        This is the test that fails if the list is ever sorted alphabetically,
        or if a general rule is appended near the top -- which is the single
        easiest way to change the baseline's score without meaning to.
        """
        cases = [
            ("Gas station on the ring road", "transport"),
            ("Gas bill for July", "housing"),
            ("Car rental for the weekend", "transport"),
            ("Rent for September", "housing"),
            ("Coffee beans, 1kg", "groceries"),
            ("Coffee and a croissant", "eating-out"),
            ("Notebook for work", "shopping"),
            ("Book about Postgres", "leisure"),
            ("Headphones", "shopping"),
            ("Phone plan", "subscriptions"),
            ("Bus ticket to the airport", "transport"),
            ("Ticket for the concert", "leisure"),
            ("Taxi home", "transport"),
            ("Income tax", "fees"),
        ]
        for description, expected in cases:
            with self.subTest(description=description):
                self.assertEqual(predict(description), expected)


class LoaderTests(unittest.TestCase):
    def written(self, text: str) -> Path:
        handle = tempfile.NamedTemporaryFile(
            "w", suffix=".csv", delete=False, encoding="utf-8", newline=""
        )
        handle.write(text)
        handle.close()
        path = Path(handle.name)
        self.addCleanup(path.unlink)
        return path

    def csv(self, *lines: str) -> Path:
        return self.written("\n".join((",".join(COLUMNS), *lines)) + "\n")

    def problems_of(self, path: Path) -> list[str]:
        with self.assertRaises(EvalSetError) as caught:
            load(path)
        return caught.exception.problems

    def test_a_good_row_loads(self):
        rows = load(self.csv("2026-08-24,12.34,EUR,Coffee and a croissant,eating-out"))

        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0].occurred_at, date(2026, 8, 24))
        self.assertEqual(rows[0].category, "eating-out")

    def test_the_amount_is_a_decimal_and_keeps_its_scale(self):
        """Money is decimal here for the same reason it is in Transaction.cs."""
        rows = load(self.csv("2026-08-24,0.10,EUR,Sweets,groceries"))

        self.assertIsInstance(rows[0].amount, Decimal)
        self.assertEqual(rows[0].amount, Decimal("0.10"))
        self.assertEqual(str(rows[0].amount), "0.10")

    def test_an_unknown_category_is_refused_and_the_vocabulary_is_printed(self):
        problems = self.problems_of(
            self.csv("2026-08-24,12.34,EUR,Coffee,eating out")  # space, not hyphen
        )

        self.assertEqual(len(problems), 1)
        self.assertIn("not in the vocabulary", problems[0])
        self.assertIn("eating-out", problems[0])

    def test_a_blank_category_is_refused_and_points_at_the_holdout(self):
        problems = self.problems_of(self.csv("2026-08-24,12.34,EUR,Coffee,"))

        self.assertEqual(len(problems), 1)
        self.assertIn("holdout", problems[0])

    def test_a_comma_decimal_separator_is_refused_rather_than_guessed(self):
        problems = self.problems_of(self.csv('2026-08-24,"12,34",EUR,Coffee,eating-out'))

        self.assertIn("not a decimal number", problems[0])

    def test_a_non_iso_date_is_refused(self):
        problems = self.problems_of(self.csv("24/08/2026,12.34,EUR,Coffee,eating-out"))

        self.assertIn("not ISO", problems[0])

    def test_a_non_finite_amount_is_refused(self):
        """Decimal("NaN") parses, and NaN answers False to every comparison."""
        problems = self.problems_of(self.csv("2026-08-24,NaN,EUR,Coffee,eating-out"))

        self.assertIn("not a finite number", problems[0])

    def test_three_decimal_places_are_refused(self):
        problems = self.problems_of(self.csv("2026-08-24,1.234,EUR,Coffee,eating-out"))

        self.assertIn("decimal places", problems[0])

    def test_a_bad_currency_is_refused(self):
        problems = self.problems_of(self.csv("2026-08-24,12.34,eur,Coffee,eating-out"))

        self.assertIn("ISO 4217", problems[0])

    def test_every_problem_is_reported_not_only_the_first(self):
        problems = self.problems_of(
            self.csv(
                "not-a-date,12.34,EUR,Coffee,eating-out",
                "2026-08-24,nope,EUR,Coffee,eating-out",
                "2026-08-24,12.34,EUR,Coffee,nonsense",
            )
        )

        self.assertEqual(len(problems), 3)

    def test_a_renamed_column_is_an_error_not_a_column_of_none(self):
        path = self.written("date,amount,currency,description,category\n")
        problems = self.problems_of(path)

        self.assertIn("header is", problems[0])

    def test_a_blank_line_between_labelling_sessions_is_not_an_error(self):
        rows = load(
            self.csv(
                "2026-08-24,12.34,EUR,Coffee,eating-out",
                "",
                "2026-08-25,5.00,EUR,Bread,groceries",
            )
        )

        self.assertEqual(len(rows), 2)

    def test_a_bom_from_excel_does_not_break_the_header(self):
        # Written as an escape rather than as the character, which is invisible
        # in an editor and is exactly the kind of thing CLAUDE.md keeps out of
        # this repository's source.
        path = self.written(
            chr(0xFEFF) + ",".join(COLUMNS) + "\n2026-08-24,12.34,EUR,Coffee,eating-out\n"
        )

        self.assertEqual(len(load(path)), 1)

    def test_a_header_only_file_loads_as_no_rows(self):
        """Which is what the eval set is today. main() turns this into exit 1."""
        self.assertEqual(load(self.csv()), [])


class BaselineTests(unittest.TestCase):
    """The `--check` comparison of #58, against hand-built reports only.

    Deliberately nothing in here asserts today's real number. That comparison is
    `python evals/score.py --check`, and it belongs there rather than in a test:
    a rule reordered by mistake has to turn CI red *on the number*, in a step
    whose message names the one file to update, rather than red on a test that
    somebody then edits until it is green again.
    """

    def baseline(self, **fields) -> Path:
        """A baseline file. The defaults describe `self.report((2, 1))`."""
        recorded = {
            "set": "transactions.csv",
            "predictor": "rules",
            "rows": 2,
            "accuracy": 0.5,
            "macro_recall": 0.5,
        }
        recorded.update(fields)
        handle = tempfile.NamedTemporaryFile(
            "w", suffix=".json", delete=False, encoding="utf-8"
        )
        json.dump(recorded, handle)
        handle.close()
        path = Path(handle.name)
        self.addCleanup(path.unlink)
        return path

    def report(self, *categories: tuple[int, int]) -> Report:
        """A Report over (rows, correct) pairs, one per category. The names do not matter."""
        scores = tuple(
            CategoryScore(name=name, rows=rows, correct=correct)
            for name, (rows, correct) in zip(CATEGORIES, categories)
        )
        return Report(
            total=sum(c.rows for c in scores),
            correct=sum(c.correct for c in scores),
            per_category=scores,
            confusions=Counter(),
        )

    def check(
        self,
        report: Report,
        baseline: Path,
        name: str = "transactions.csv",
        predictor: str = "rules",
    ):
        """Run the comparison, returning its exit code and what it said."""
        stderr = io.StringIO()
        with redirect_stderr(stderr):
            code = check(report, Path(name), baseline, predictor)
        return code, stderr.getvalue()

    def test_a_report_that_reproduces_the_baseline_passes(self):
        code, _ = self.check(self.report((2, 1)), self.baseline())

        self.assertEqual(code, 0)

    def test_a_number_that_moved_is_exit_2(self):
        code, said = self.check(self.report((2, 2)), self.baseline())

        self.assertEqual(code, 2)
        self.assertIn("100.0%", said)

    def test_the_two_numbers_are_compared_separately(self):
        """Accuracy and macro recall disagree by design, so one may move alone.

        Four rows in one category all correct and one row in another wrong is
        80% accuracy and 50% macro; three of four and one of one is 80% accuracy
        and 87.5% macro. So each of them can be the only one that moved, and a
        check that compared one number would be silent about the other.
        """
        macro_moved, about_macro = self.check(
            self.report((4, 4), (1, 0)),
            self.baseline(rows=5, accuracy=0.8, macro_recall=0.8),
        )
        accuracy_moved, about_accuracy = self.check(
            self.report((4, 3), (1, 1)),
            self.baseline(rows=5, accuracy=0.9, macro_recall=0.875),
        )

        self.assertEqual(macro_moved, 2)
        self.assertIn("macro_recall", about_macro)
        self.assertNotIn("accuracy", about_macro)

        self.assertEqual(accuracy_moved, 2)
        self.assertIn("accuracy", about_accuracy)
        self.assertNotIn("macro_recall", about_accuracy)

    def test_a_row_count_that_moved_is_reported_even_when_the_numbers_did_not(self):
        """A CSV that gained rows and scored the same is a changed eval set.

        Legitimate, and not the same event as a rule that broke -- which is why
        it is checked at all, and why it is named separately in the message.
        """
        code, said = self.check(self.report((4, 2)), self.baseline())

        self.assertEqual(code, 2)
        self.assertIn("rows", said)

    def test_the_comparison_is_the_number_as_printed_and_not_the_float(self):
        """One decimal place of a percentage, which is what is written down.

        50.04% reproduces a recorded 50.0%; 50.1% does not. Comparing the floats
        would make an invisible rounding difference a red build, and comparing
        anything coarser would let a row's worth of drift through.
        """
        recorded = self.baseline(rows=10_000)

        unchanged, _ = self.check(self.report((10_000, 5_004)), recorded)
        moved, _ = self.check(self.report((10_000, 5_010)), recorded)

        self.assertEqual(unchanged, 0)
        self.assertEqual(moved, 2)

    def test_a_baseline_recorded_against_another_set_is_refused_rather_than_compared(self):
        """--set holdout.csv must not be scored against transactions.csv's number."""
        code, said = self.check(self.report((2, 1)), self.baseline(), name="holdout.csv")

        self.assertEqual(code, 1)
        self.assertIn("nothing to compare", said)

    def test_a_baseline_that_cannot_be_read_is_exit_1_and_not_exit_2(self):
        """The distinction the three exit codes exist for.

        A deleted or malformed baseline is a broken check, not a moved number,
        and whoever reads the red step reacts to the two differently.
        """
        malformed = self.baseline()
        malformed.write_text("{not json", encoding="utf-8")

        missing, _ = self.check(self.report((2, 1)), Path("no-such-baseline.json"))
        unreadable, _ = self.check(self.report((2, 1)), malformed)

        self.assertEqual(missing, 1)
        self.assertEqual(unreadable, 1)

    def test_a_baseline_missing_a_number_is_exit_1(self):
        recorded = self.baseline()
        recorded.write_text(json.dumps({"set": "transactions.csv"}), encoding="utf-8")

        code, said = self.check(self.report((2, 1)), recorded)

        self.assertEqual(code, 1)
        self.assertIn("macro_recall", said)

    def test_a_model_run_is_not_compared_against_the_rules_baseline(self):
        """#60's guard, and the reason it is exit 1 rather than exit 2.

        The whole point of the issue is that the two predictors score
        differently. Comparing one against the other's recorded number would
        report the improvement as drift, in a message telling whoever read it to
        update the baseline -- which would overwrite the rules number with the
        model's and destroy the only thing there is to compare against.
        """
        code, said = self.check(self.report((2, 2)), self.baseline(), predictor="model")

        self.assertEqual(code, 1)
        self.assertIn("nothing to compare", said)

    def test_a_baseline_that_does_not_say_which_predictor_is_exit_1(self):
        """A number with no predictor beside it is not a baseline, it is a number."""
        recorded = self.baseline()
        recorded.write_text(
            json.dumps(
                {"set": "transactions.csv", "rows": 2, "accuracy": 0.5, "macro_recall": 0.5}
            ),
            encoding="utf-8",
        )

        code, said = self.check(self.report((2, 1)), recorded)

        self.assertEqual(code, 1)
        self.assertIn("predictor", said)

    def test_the_shipped_baseline_names_a_predictor_that_exists(self):
        recorded = json.loads(BASELINE.read_text(encoding="utf-8"))

        self.assertIn(recorded["predictor"], PREDICTORS)

    def test_the_shipped_baseline_describes_the_default_eval_set(self):
        """Not the number -- the file it claims to be about.

        A name that matches nothing makes every run exit 1 rather than 2, so it
        fails as a broken check instead of as drift; cheap to assert here, and
        it is the one thing about the shipped file that is not data.
        """
        recorded = json.loads(BASELINE.read_text(encoding="utf-8"))

        self.assertEqual(recorded["set"], DEFAULT_SET.name)


class PredictorTests(unittest.TestCase):
    """`build_predictor`, and the one property that makes #60's widening safe."""

    def build(self, name: str):
        predictor, label = build_predictor(name, {}, total=0)
        return predictor, label

    def test_the_rules_predictor_reads_the_description_and_nothing_else(self):
        """Why widening the seam to a whole Row could not move the baseline.

        `score` used to hand over a description; it now hands over the row,
        because the model is shown the amount and the currency. That is only safe
        if the rules ignore everything the description does not carry -- asserted
        here rather than argued, and asserted again on every pull request by
        `--check` reproducing 56.1%.
        """
        predictor, _ = self.build("rules")
        cheap = Row(
            line=1,
            occurred_at=date(2026, 8, 28),
            amount=Decimal("4.50"),
            currency="MDL",
            description="pharmacy",
            category="health",
        )
        expensive = Row(
            line=2,
            occurred_at=date(2019, 1, 1),
            amount=Decimal("450.00"),
            currency="USD",
            description="pharmacy",
            category="health",
        )

        self.assertEqual(predictor(cheap), predict("pharmacy"))
        self.assertEqual(predictor(cheap), predictor(expensive))

    def test_the_label_names_where_the_rules_actually_live(self):
        """The header used to say `evals/rules.py`, which #39 moved.

        A label typed into the renderer describes whichever predictor existed
        when it was typed. This one is built beside the predictor it describes,
        and the assertion is that the path in it is real.
        """
        _, label = self.build("rules")

        self.assertIn(str(len(RULES)), label)
        self.assertTrue(
            (Path(__file__).resolve().parents[1] / label.rsplit(", ", 1)[1]).exists()
        )


class RenderingTests(unittest.TestCase):
    """The two things #60 asks for beside the percentage."""

    def report(self) -> Report:
        rows = [row("groceries"), row("groceries"), row("other", "parcel by post")]
        return score(
            rows,
            lambda r: "groceries" if r.category == "groceries" else NO_PREDICTION,
        )

    def test_the_matrix_has_a_column_for_every_category_plus_the_abstention(self):
        """A fixed shape, so two runs can be read side by side.

        Columns derived from the answers would give each predictor a different
        table, which is the one thing a comparison cannot have.
        """
        header = render_confusion(self.report()).splitlines()[4]

        for name in CATEGORIES:
            self.assertIn(name[:3], header)
        self.assertIn(NO_PREDICTION[:3], header)

    def test_the_matrix_puts_correct_answers_on_the_diagonal(self):
        matrix = render_confusion(self.report())
        groceries = next(
            line for line in matrix.splitlines() if line.startswith("groceries")
        )
        other = next(line for line in matrix.splitlines() if line.startswith("other"))

        # Two correct in the groceries column, and nothing anywhere else.
        self.assertEqual(groceries.split()[1:], ["2"] + ["."] * 11)
        # Nothing anywhere in the vocabulary, and the one row in the abstention.
        self.assertEqual(other.split()[1:], ["."] * 11 + ["1"])

    def test_the_misses_listing_names_the_description(self):
        listing = render_misses(self.report())

        self.assertIn("parcel by post", listing)
        self.assertIn("other", listing)
        self.assertIn(NO_PREDICTION, listing)


if __name__ == "__main__":
    unittest.main(verbosity=2)
