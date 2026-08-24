# evals

The eval set, the metric and the rules baseline for the categorizer -- #25.

**The decisions live in [`../docs/evals.md`](../docs/evals.md)**: the eleven
categories and the boundary rules for the awkward rows, what the metric is and
what it does not capture, and why the baseline abstains instead of guessing.
This file is only how to run it and how to label.

Nothing here has a dependency. Python 3.12 or newer, stdlib only; the `uv`
project CLAUDE.md plans arrives with the categorizer service in #39.

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

`transactions.csv` holds a header and no rows, so `score.py` exits 1 and says
so. **That is the honest state, not a bug.** The half of #25 that is left is the
half nobody else can do: 30-50 transactions from real spending, labelled by
hand. Everything around it -- the vocabulary, the metric, the baseline, the
scorer and its tests -- is here and checked.

## How to label

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
