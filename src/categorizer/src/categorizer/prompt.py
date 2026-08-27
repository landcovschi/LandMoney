"""What the model is told, and the schema its answer has to fit.

Its own module rather than string constants inside the adapter, for one reason
that is about #60 rather than about tidiness: the prompt is the thing that will
be edited when the score is not good enough, and a file whose whole content is
"what the model was told" makes that edit visible in a diff instead of buried
among error handling.

**The vocabulary is built from `CATEGORIES`, never retyped.** That is the same
rule `contracts.py` follows for the response enum, and it matters more here: a
prompt naming ten of the eleven categories would produce a model that never
answers the eleventh, and nothing would fail -- the score would simply be lower,
in a way that reads like the model being bad at the task. `test_prompt.py`
asserts every category appears.

The descriptions and the boundary rules below are copied from `docs/evals.md`,
section 1, which is where they were decided and which stays the source. They are
here rather than in `categories.py` on purpose: `categories.py` is imported by
`evals/score.py`, and the scorer must not grow a dependency on how a predictor
is prompted -- what the labels *mean* is a property of the eval set, and what
the model is *told* is a property of one predictor being scored against it.
"""

from typing import Final

from categorizer.categories import CATEGORIES, NO_PREDICTION

# One line per category, keyed by the value in CATEGORIES so a missing key is an
# error at import rather than a category the model is never told about.
#
# Wording taken from the table in docs/evals.md rather than reworded, so that a
# disagreement between what a labeller was told and what the model is told cannot
# open up quietly -- it would show as a diff in one of the two files.
_WHAT_BELONGS: Final[dict[str, str]] = {
    "groceries": "Food and household consumables bought to take home: supermarket, market, bakery, cleaning supplies.",
    "eating-out": "Food and drink consumed away from home: restaurant, cafe, bar, takeaway, delivery.",
    "transport": "Moving a person around: fuel, taxi, public transport, parking, car servicing, insurance for the car.",
    "housing": "Costs attached to the flat: rent, electricity, gas, water, heating, home internet, repairs.",
    "health": "Pharmacy, doctor, dentist, optician, medical insurance.",
    "shopping": "Durable goods for personal use: clothes, shoes, electronics, furniture, homeware.",
    "subscriptions": "Recurring services attached to the person: streaming, cloud, software, mobile plan, gym.",
    "leisure": "Experiences: cinema, concerts, books, games, hobbies, and travel.",
    "gifts": "Money or goods for someone else, and charity.",
    "fees": "Bank charges, transfer and conversion fees, taxes, fines, government fees.",
    "other": "Genuinely fits nothing above.",
}

# The awkward rows, verbatim from docs/evals.md, "The boundary rules". Their whole
# job there is that a vocabulary is only closed if the hard cases have one answer;
# their job here is the same, aimed at a different reader. Without them the model
# is being scored against a rubric it was never shown, which measures whether it
# happened to guess this project's conventions.
_BOUNDARY_RULES: Final[tuple[str, ...]] = (
    "Home internet is housing; the mobile plan is subscriptions. The test is what the service is "
    "attached to -- one follows the flat, the other follows the person.",
    "A holiday is leisure, including the flight and the hotel. transport is routine movement; a trip "
    "is an experience.",
    "Coffee beans from a supermarket are groceries; a coffee in a cafe is eating-out. Where it is "
    "consumed, not what it is.",
    "A book is leisure, a laptop is shopping. Consumed once versus owned.",
    "Car insurance is transport, health insurance is health. Insurance is not a category; it is an "
    "attribute of the thing insured.",
    "other is for a transaction that fits nothing, never for one that is hard to place between two. "
    "Use the rules above for those.",
)


def _categories_block() -> str:
    return "\n".join(f"- {name}: {_WHAT_BELONGS[name]}" for name in CATEGORIES)


def _boundary_block() -> str:
    return "\n".join(f"- {rule}" for rule in _BOUNDARY_RULES)


# Abstention is stated as an instruction and not merely permitted by the schema.
#
# #59 asks for it explicitly, and the reason is the metric rather than manners: a
# model that guesses when it does not know converts an abstention into a confident
# error, and macro recall charges the same for both while the .NET side stores the
# wrong one. It is also what makes this comparable to the baseline at all -- the
# rules abstain on 22 of their 23 misses, so a model forbidden to abstain is being
# scored on a different task.
#
# `unknown` is reused rather than a new sentinel invented, because it is what
# `rules.py` already returns and what `categories.py` keeps outside the vocabulary
# so the scorer counts it as a miss. One sentinel, one meaning, both predictors.
SYSTEM_PROMPT: Final[str] = f"""\
You categorise one personal spending transaction into exactly one category from a \
closed list.

The categories, and what belongs in each:
{_categories_block()}

Boundary rules. These decide the cases that would otherwise go two ways:
{_boundary_block()}

Answer with one of those category names, exactly as written above.

If the description does not give you enough to decide, answer "{NO_PREDICTION}". \
Do this rather than guessing: a wrong category is worse than no category here, \
because no category is a state the application already handles and a wrong one is \
stored as if it were true. Note that "{NO_PREDICTION}" is not a category -- it \
means you are declining, and it is counted as a miss.

The amount and the currency are given because they separate purchases the \
description alone does not: a 4.50 MDL and a 450 MDL entry at the same merchant \
are not the same kind of spending.

This is one transaction with no conversation around it. Do not ask questions and \
do not explain your reasoning."""

# The answer's shape, enforced by the API rather than by parsing prose.
#
# The enum is CATEGORIES plus the sentinel, so "outside the vocabulary" is a state
# the model cannot reach through this path at all -- which is the point of
# constraining it. The adapter still validates the value it gets back, and that is
# not redundant: this schema constrains one route to one API, and the check in the
# adapter is what holds if the route changes, if a fallback model is added, or if
# the response arrives through anything else.
RESPONSE_SCHEMA: Final[dict[str, object]] = {
    "type": "object",
    "properties": {
        "category": {
            "type": "string",
            "enum": [*CATEGORIES, NO_PREDICTION],
        },
    },
    "required": ["category"],
    "additionalProperties": False,
}
