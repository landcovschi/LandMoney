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

import hashlib
from typing import Final, Sequence

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
_BASE_PROMPT: Final[str] = f"""\
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

# What the model is told about the retrieved rows -- #66.
#
# **Appended only when there are examples**, which is what keeps two things true at
# once: the prompt a request was answered under is always the prompt describing what
# that request contained, and the no-examples prompt is byte-for-byte the one #60
# measured at 98.9%. A single prompt carrying this paragraph unconditionally would
# have been simpler and would have re-labelled the recorded number, so the "off" arm
# of #66's own comparison would have had to be bought again at 53 API calls.
#
# The second paragraph is the one that earns its place. Neighbours are retrieved by
# similarity, which is not relevance: a corpus of 53 rows returns five of them for
# every query however unlike they are, and `LexicalStore` drops only the rows sharing
# no three characters at all. Told nothing, a model shown five confident-looking
# labelled rows will reach for the majority; told this, it can decline them. That is
# the difference between examples and contamination, and it is #66's first trap
# pointed at the prompt rather than at the corpus.
_EXAMPLES_INSTRUCTION: Final[str] = """\
Below the transaction you will be shown a few of this person's own past \
transactions, with the categories they gave them themselves. They were chosen \
because their descriptions resemble this one.

Treat them as the best available evidence about what these particular words mean \
to this person -- a shop name carries no meaning of its own, and these rows are \
where its meaning is recorded.

They were chosen by similarity and not by relevance, so some of them may have \
nothing to do with this transaction. Judge each one. A past transaction that is \
plainly the same kind of purchase should decide your answer; one that merely shares \
a word should be ignored, and being shown examples is never a reason to stop \
answering "unknown" when you do not know."""


def system_prompt(with_examples: bool) -> str:
    """The instructions, with the paragraph about examples only when there are some.

    A function rather than two constants at the call site so that "which prompt was
    sent" and "which fingerprint keyed the cache" cannot be answered from different
    places -- `fingerprint` below takes the same argument and is the only other
    reader.
    """
    return _BASE_PROMPT + ("\n\n" + _EXAMPLES_INSTRUCTION if with_examples else "")


# Unchanged bytes, and that is asserted rather than hoped: `test_prompt.py` pins
# this to sha256:c8ad9d9fd16f, which is the digest #60 recorded beside 98.9% and the
# one every cache key written since #65 carries. If this moves, the recorded number
# is describing a prompt that no longer exists.
SYSTEM_PROMPT: Final[str] = system_prompt(False)

# What this prompt is, in twelve hex characters. Two things read it, and it lives
# here rather than in either of them because they must never disagree.
#
# `evals/score.py` prints it in the header above the score -- #60's half that is
# not a percentage, since a score with no record of the prompt beside it is not
# reproducible. And `cache.py` puts it in every key, which is #65's second trap:
# without it, the first prompt edit would serve yesterday's answers for ever and
# the eval run after that edit would measure a cache rather than a model.
#
# One string doing both is what makes those two facts one fact -- an edited prompt
# both re-labels the score and invalidates every cached answer, in the same commit,
# with nothing to remember.
#
# Twelve characters of a sha256, which is 48 bits: ample for telling two prompts
# apart, and short enough to read out of a report header.
#
# What it deliberately does not cover: RESPONSE_SCHEMA. A vocabulary change moves
# this digest anyway, because the category names appear verbatim in the block
# above -- but a change to the schema's *shape* alone would not, and would need the
# `v1` in `cache.py`'s key prefix.
def fingerprint(with_examples: bool) -> str:
    """Twelve hex characters of whichever prompt was actually sent.

    **Two prompts mean two fingerprints, and that is the whole mechanism rather
    than bookkeeping.** #65 put this digest in every cache key so an edited prompt
    could not serve yesterday's answers; #66 adds a second way for the prompt to
    differ, and it differs *per request* rather than per commit. Without the
    argument, a description answered with five examples and the same description
    answered with none would share a key -- and since the examples are also in the
    user message the key would in fact differ, which is worse: it would differ for
    the right reason under a label that lied about which instructions produced it.
    """
    return hashlib.sha256(system_prompt(with_examples).encode("utf-8")).hexdigest()[:12]


FINGERPRINT: Final[str] = fingerprint(False)
FINGERPRINT_WITH_EXAMPLES: Final[str] = fingerprint(True)


def render_examples(pairs: "Sequence[tuple[str, str]]") -> str:
    """The retrieved rows as the model sees them, nearest first.

    Takes descriptions and categories rather than `Neighbour`s so that `prompt.py`
    keeps importing nothing from `retrieval.py` -- the scores are for the log and
    for `--show-examples`, and a number the model was never shown has no business
    in the file that decides what the model is shown.

    **The score is deliberately not rendered.** Told "0.31", a model has to invent
    a policy about what that number means, and it would be a different policy for
    cosine than for trigram overlap -- so the same prompt would mean two things
    depending on which store was configured. The instruction above says to judge
    each row on its own, which is a job it can do from the text.
    """
    lines = "\n".join(f'- "{description}" -> {category}' for description, category in pairs)
    return f"This person's own past transactions, most similar first:\n{lines}"

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
