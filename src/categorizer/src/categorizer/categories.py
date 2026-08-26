"""The closed category vocabulary.

This is the single place the list exists. `score.py` refuses a CSV containing
anything not in here, so a misspelt label is an error rather than a twelfth
category quietly joining the vocabulary -- which is the failure that makes a
metric stop meaning anything.

Moved out of `evals/` in #39, unchanged. Two things read it now: the
scorer, and `contracts.py`, which builds the API's response enum from
`CATEGORIES` rather than restating the list. A twelfth category is therefore one
edit here plus one in `docs/evals.md`, and never a string typed into a schema.

The reasoning behind the eleven, the boundary rules for the awkward rows, and
what was left out is in `docs/evals.md`. Do not add a category without adding
it there too: a vocabulary decided halfway through labelling is not closed.
"""

from typing import Final

# Declaration order is display order in the scorer's table. Nothing else reads
# it, so it is grouped by how often the owner is expected to meet a category
# rather than alphabetically -- the top of the table should be the rows that
# dominate a month.
CATEGORIES: Final[tuple[str, ...]] = (
    "groceries",
    "eating-out",
    "transport",
    "housing",
    "health",
    "shopping",
    "subscriptions",
    "leisure",
    "gifts",
    "fees",
    "other",
)

# Membership is asked once per row per run, so the set is not an optimisation --
# it is here so that `in CATEGORIES` cannot be written against the tuple by
# accident and turn a validation loop quadratic when the set grows.
KNOWN: Final[frozenset[str]] = frozenset(CATEGORIES)

# What a predictor returns when it has no answer. Deliberately NOT a member of
# CATEGORIES, so it is always scored as a miss -- see docs/evals.md, "the rules
# baseline", for why abstaining does not get a discount and why falling back to
# the majority category was rejected.
NO_PREDICTION: Final[str] = "unknown"

# Below this many rows a category's recall is a coin flip rather than a
# measurement: at 2 rows the only possible scores are 0%, 50% and 100%. The
# scorer names any category under it. Three is the floor, five is the target;
# the argument is in docs/evals.md under "what was left out".
MIN_ROWS_PER_CATEGORY: Final[int] = 3
