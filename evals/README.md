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

```bash
uv run --project src/categorizer python evals/score.py --predictor model --confusion --misses
```

The first three work from the repository root, and only from there -- `score.py` finds
the categorizer package by a path relative to its own file, and `test_score.py`
imports `score` from its own folder. `score.py` prints a per-category table, the
accuracy and the macro recall, and exits 0 when it produced a number and 1 when
it could not -- an unreadable file, a label outside the vocabulary, or a set
with no rows. A scorer that answers 0.0% when it scored nothing is worse than
one that refuses.

The fourth is #60 and is the only command here that is not free. `--predictor
model` sends **one API call per row** and nothing caches, so a run over
`transactions.csv` is 53 calls; it needs `ANTHROPIC_API_KEY` in the environment,
and it borrows the categorizer's virtual environment because the `anthropic`
package lives there. Everything else in this folder stays stdlib-only, which is
what `--predictor rules` -- the default -- still proves on every CI run.

**Getting the key into that environment is its own trap, and the answer is one
line.** `docker compose` reads `.env` by itself; `uv run` does not, so a key
written into `.env` reaches the container and not the scorer, and the run stops
with `no Anthropic credential was found`. Rather than exporting it into a shell
-- a Windows user variable is read by a process at start, so anything already
running keeps the old environment -- source the file for that command alone:

```bash
set -a && . ./.env && set +a && uv run --project src/categorizer python evals/score.py --predictor model --confusion --misses
```

`score.py` still parses nothing: the shell does the loading. Measured on
2026-08-28, that run is 53 calls in about 114 s -- ~2.1 s each, inside the
adapter's 6-second timeout, which is why nothing failed.

**It refuses rather than scoring low when calls fail.** `AnthropicPredictor`
never raises: every failure is a null category, which is what protects a user's
transaction on the .NET side and which here is indistinguishable from the model
declining a row. So the scorer counts the adapter's ERROR records and exits 1
without printing anything if there are any -- a missing key stops it before the
first call rather than producing a 0% that reads like a bad model. A warning
about an unusable *answer* is a real miss and stays in the number.

`--confusion` prints the full matrix and `--misses` prints every missed row with
its description. Both are for reading a result: the metric charges the same for
an abstention and a confident error, and #60 asks for those to be reported apart
from the percentage. The line under the score does the same job in one number.

## The baseline, and when it may move

`--check` compares the run against **`baseline.json`** and exits **2** when the
number is not the one recorded there. That is what CI runs on every pull
request -- #58 -- and it is the difference between running the scorer and
checking it: printing a number is the whole of what exit code 0 promises, so a
step that merely runs `score.py` stays green while the answer drifts.

`baseline.json` is the **only** place the number is asserted, and since #60 it
also records **which predictor** produced it. `--check` refuses to compare a
model run against it -- exit 1, "nothing to compare", the same refusal a run
against the holdout gets. Without that guard the improvement the whole slice
exists to produce would be reported as drift, in a message inviting whoever read
it to overwrite the rules number with the model's.
 Nothing in
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
and `holdout-spent-2026-08-29.csv` holds 10 more that are **labelled and
spent** -- #66 scored both predictors on them on 2026-08-29 (rules 44.4%, model
100.0%, over the nine of eleven categories the file covers) and #91 recorded it
burned on 2026-09-02, renaming it so a copied command fails rather than scores
it twice. There is no holdout right now; section 4 of `docs/evals.md` is the
account. The baseline
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
5. **Hold a few rows back, in a file of their own, with the category column
   empty.** Tuning against the same fifty rows until they pass teaches the rules
   the answers, and the eval set cannot report that it has happened. `score.py`
   refuses a file with blank labels, so spending them has to be deliberate
   rather than accidental.

   **Take them before the labelling session, not after it.** Once a set has been
   scored against, no part of it is held out any more, and carving a holdout out
   of it afterwards produces rows that were already seen. This is the one step
   here that cannot be done later.

   There is no such file today. The old one is `holdout-spent-2026-08-29.csv`,
   named for what happened to it, and the replacement is a slice of the real
   export from #90 -- which is why the name `holdout.csv` is free again.

## Getting rows out of the application

#89. Correcting a category in the interface stores `category_source = human`
(#63), which is a labelled row produced by the one person who can judge it,
during ordinary use. Until #89 those rows accumulated in Postgres and there was
no way out; `GET /api/transactions/labelled` is the way out, and the
**Export labelled rows** card on the screen is the button that calls it.

It answers `text/csv` with the five columns below, ordered oldest first, and
**only the rows whose source is `human`**. That filter is the point of the whole
endpoint rather than a detail of it: a `rules` or `model` row exported into this
set is the predictor grading its own past answers, and the number afterwards
means nothing while the file looks exactly right.

The file is a valid eval set on its own, which is worth doing before merging it
into this one -- it says what the baseline makes of rows nobody wrote for it:

```bash
python evals/score.py --set ~/Downloads/labelled-2026-08-31.csv
```

To add the rows here, append it **without its header line**:

```bash
tail -n +2 ~/Downloads/labelled-2026-08-31.csv >> evals/transactions.csv
```

Then re-run the scorer and update `baseline.json` in the same commit, per the
section above -- rows added to a CSV are one of the three things that are meant
to move the number, and CI turns red until the recorded one moves with them.

Two things the export does not do. It **does not deduplicate against what is
already here**, so exporting twice and appending twice puts every row in twice;
the fix is to append once and to know which export you last merged, which is why
the file is named after the day it was taken. And it exports the *latest* label
and no history -- a row corrected twice appears once, because `PATCH` updates the
row in place and there is no journal.

This is not the file `POST /api/transactions/import` reads. That one has four
columns and no category, and it is how a bank export gets *into* the
application; this one has five and is how labels get out. They are different
files with different jobs, which is why nothing calls this one `transactions.csv`.

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

`score.score(rows, predictor)` takes any `Row -> str`, and `build_predictor`
turns `--predictor rules` or `--predictor model` into one. Nothing in `score.py`
knows about either implementation beyond that function.

**It used to take a `str -> str`, and #60 widened it.** This file argued against
that on the grounds that "the metric is about the description", and that argument
lost to a stronger one: the model is *shown* the amount and the currency --
`prompt.py` says so, because a 4.50 and a 450 at the same merchant are not the
same purchase -- so a scorer that could only hand over a description would be
measuring a different predictor from the one the service runs. That is the drift
#39 moved `rules.py` out of this folder to prevent, and it is invisible: the
number would simply have been lower.

The widening could not move the baseline, because the rules read nothing but the
description. That is a test (`test_the_rules_predictor_reads_the_description_and_
nothing_else`) and it is also what `--check` asserts on every pull request: 56.1%
reproduced across the change, or the change was wrong.

The **second** seam is still a different one and still deliberately so.
`Predictor` in `src/categorizer/src/categorizer/predictor.py` is what the
*service* plugs a model into: it takes the whole request and returns the
`source`, so a predictor names itself rather than being labelled by
configuration. `build_predictor` adapts one to the other in six lines -- and the
only thing it translates is the abstention, which crosses HTTP as `null` and has
to be the `unknown` sentinel again by the time the metric sees it.
