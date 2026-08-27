# evals

The eval set, the metric and the rules baseline for the categorizer -- #25.

**The decisions live in [`../docs/evals.md`](../docs/evals.md)**: the eleven
categories and the boundary rules for the awkward rows, what the metric is and
what it does not capture, and why the baseline abstains instead of guessing.
This file is only how to run it and how to label.

Nothing here has a dependency. Python 3.12 or newer, stdlib only -- and that
stayed true after #39, which is the point of the `sys.path` line at the top of
`score.py`.

**The rules are no longer in this folder.** `categories.py` and `rules.py` moved
to `src/categorizer/src/categorizer/` in #39 and did not change a character.
`score.py` reaches them by putting that folder on `sys.path` rather than by
installing the package, so `python evals/score.py` still needs no `uv`, no
virtual environment and no network. What the move buys: **the scorer and the
deployed service run the same `predict`**, so the baseline number is a statement
about what the API answers rather than about a copy of it that can drift.

The consequence to know before editing a rule: it now moves the baseline **and**
the deployed answer at once.

## Run it

```bash
python evals/score.py
```

```bash
python evals/score.py --check
```

```bash
python evals/test_score.py
```

All three work from the repository root, and only from there -- `score.py` finds
the categorizer package by a path relative to its own file, and `test_score.py`
imports `score` from its own folder. `score.py` prints a per-category table, the
accuracy and the macro recall, and exits 0 when it produced a number and 1 when
it could not -- an unreadable file, a label outside the vocabulary, or a set
with no rows. A scorer that answers 0.0% when it scored nothing is worse than
one that refuses.

## The baseline, and when it may move

`--check` compares the run against **`baseline.json`** and exits **2** when the
number is not the one recorded there. That is what CI runs on every pull
request -- #58 -- and it is the difference between running the scorer and
checking it: printing a number is the whole of what exit code 0 promises, so a
step that merely runs `score.py` stays green while the answer drifts.

`baseline.json` is the **only** place the number is asserted. Nothing in
`test_score.py` knows today's score: the tests there cover the comparison
against hand-built reports, so a rule reordered by mistake turns CI red on the
number rather than red on a test somebody then edits until it is green.

The number is meant to move when, and only when, a change makes it move on
purpose:

- a rule added, removed or reordered in
  `src/categorizer/src/categorizer/rules.py`,
- a change to the vocabulary or to a boundary rule in `docs/evals.md`,
- **a change to `transactions.csv`** -- the CSVs are data, and rows added,
  removed or relabelled legitimately move the score. The row count is recorded
  beside the two percentages so this is reported as an eval set that changed
  rather than as a rule that broke.

In all three cases `baseline.json` is updated **in the same change**, and the
new number belongs in the pull request that moved it. What it is not for is
copying a number out of a red CI step to make it green: that is the drift the
check exists to catch, and the per-category table printed above the failure is
what says which rule did it.

Both percentages are compared as they are printed -- one decimal place -- which
is how they are written down here and in `CLAUDE.md`. One row of 53 is 1.9
points, so nothing a rule can do hides inside that rounding.

`test_score.py` is run as a script rather than through `python -m unittest`,
because `score.py` imports `categories` and `rules` as top-level modules and it
is the script's own folder that lands on `sys.path`. That is the price of having
no package yet.

## The state of this today

`transactions.csv` holds **53 labelled rows and none of them are real spending**,
and `holdout.csv` holds 10 more with the category column empty. The baseline
scores **56.1% macro recall** and 56.6% accuracy against them -- which is what
`baseline.json` records and what CI asserts.

**#47 is still open**, and this is the second set to leave it open. #25 asked for
transactions out of the owner's own history; PR #44 left the files empty rather
than invent them; on 2026-08-25 the owner asked for rows to be written instead,
and on 2026-08-26 asked for them to be written again rather than supplied. The
rows were rewritten to be typed rather than composed -- lower case, real
merchant names, a typo carried over from the deployed database, an MDL-dominant
currency mix -- and that is a real improvement over the first set and is not
what #47 asks for.

Read the number with `docs/evals.md` **section 6** open, which is where what
changed and what did not is written down. The short version: the label
distribution is still chosen rather than observed, because the three-rows-per-
category floor and a realistic shape cannot both hold at this size; the labels
were still produced by the same kind of thing slice 4 will be scored against;
and the descriptions are still English, which is now a standing decision rather
than an oversight and is the single most likely way this baseline reads
optimistic.

Replacing these rows with real ones is still a change to two CSV files and
nothing else -- the loader, the metric and the rules do not know where a row
came from.

Everything around the data -- the vocabulary, the metric, the baseline, the
scorer and its tests -- was written on 2026-08-24, before any of it. The
nine that check the baseline comparison arrived with #58 on 2026-08-28;
the other 26 did not change.

## How to label

The rows that are there were written against these instructions and break the
first of them. When they are replaced with real ones, this is the list to follow.

1. **Take the descriptions from real spending**, as they are really typed. The
   terse ones and the ambiguous ones are the valuable rows; a set of only the
   easy ones produces a number that is meaningless in both directions.
2. **Read the eleven categories and the boundary rules in `docs/evals.md`
   first**, and label against them rather than against instinct. Where instinct
   and the written rule disagree, the rule is what is wrong -- change it there,
   then relabel. A vocabulary decided halfway through labelling is not closed.
3. **Aim for at least three rows in every category you use, five where you
   can.** At two rows a category's recall can only be 0%, 50% or 100%, and the
   macro average takes that seriously. `score.py` names any category under
   three.
4. **Do not look at any prediction while labelling.** Not the model's, and not
   `python evals/score.py` either. Seeing an answer and then deciding whether it
   is acceptable is how a 60% system scores 90%.
5. **Put a few rows in `holdout.csv` and leave the category column empty.**
   They are not labelled now and not looked at again until the end of slice 4.
   Tuning against the same fifty rows until they pass teaches the rules the
   answers, and the eval set cannot report that it has happened. `score.py`
   refuses a file with blank labels, so using them early has to be deliberate.

## The CSV

```
occurred_at,amount,currency,description,category
2026-08-24,12.34,EUR,Coffee and a croissant,eating-out
```

The `Transaction` entity minus the fields a machine fills in. Same rules as the
application, and the loader enforces them rather than trusting them:

- `occurred_at` is ISO `yyyy-mm-dd`, no time. `24/08/2026` is refused.
- `amount` is a positive decimal with at most two places and a `.` separator.
  `12,34` is refused rather than silently read as something else -- the same
  invariant-parsing rule the .NET side has been bitten by twice.
- `currency` is a three-letter upper-case ISO 4217 code: `EUR`, `MDL`, `USD`.
- `category` must be one of the eleven. A misspelling is an error, not a twelfth
  category.

A description containing a comma needs quoting, which any editor writing CSV
does for you. Blank lines between labelling sessions are allowed. Excel's UTF-8
BOM is handled.

## Where the model plugs in

`score.score(rows, predictor)` takes any `str -> str`. `rules.predict` is one;
the Anthropic adapter of slice 4 is another, and the day it exists that function
is what puts the two numbers side by side. Nothing in `score.py` knows about
rules beyond the default it passes in `main`.

There is now a **second** seam, and they are deliberately not the same one.
`Predictor` in `src/categorizer/src/categorizer/predictor.py` is what the
*service* plugs a model into: it takes the whole request, because a model can
use the amount and the currency where substring matching cannot, and it returns
the `source` so a predictor names itself rather than being labelled by
configuration. This one stays `str -> str` because the metric is about the
description; widening it would change what the baseline was measured on.
