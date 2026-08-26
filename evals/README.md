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
deployed service run the same `predict`**, so 60.8% is a statement about what
the API answers rather than about a copy of it that can drift.

The consequence to know before editing a rule: it now moves the baseline **and**
the deployed answer at once.

## Run it

```bash
python evals/score.py
```

```bash
python evals/test_score.py
```

Both work from the repository root. `score.py` prints a per-category table, the
accuracy and the macro recall, and exits 0 when it produced a number and 1 when
it could not -- an unreadable file, a label outside the vocabulary, or a set
with no rows. A scorer that answers 0.0% when it scored nothing is worse than
one that refuses.

`test_score.py` is run as a script rather than through `python -m unittest`,
because `score.py` imports `categories` and `rules` as top-level modules and it
is the script's own folder that lands on `sys.path`. That is the price of having
no package yet.

## The state of this today

`transactions.csv` holds **45 labelled rows and none of them are real spending**,
and `holdout.csv` holds 8 more with the category column empty. #25 asks for
transactions out of the owner's own history; PR #44 left the files empty rather
than invent them, and on 2026-08-25 the owner asked for them to be written
instead. The baseline scores **60.8% macro recall** against them.

Read that number with `docs/evals.md` section 5 open, which is where the four
things it does not mean are written down. The short version: the label
distribution was chosen rather than observed, the descriptions are English, and
the labels were produced by the same kind of thing slice 4 is going to score
against them. Replacing these rows with real ones is a change to two CSV files
and nothing else -- the loader, the metric and the rules do not know where a row
came from.

Everything around the data -- the vocabulary, the metric, the baseline, the
scorer and its 26 tests -- was written on 2026-08-24, before any of it.

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
description; widening it would change what the 60.8% was measured on.
