"""The prompt, checked against the vocabulary it is supposed to carry -- #59.

This file is short and it guards the failure mode that is hardest to see: a prompt
that omits a category produces a model that never answers it, and **nothing breaks**.
The service returns 200s, the .NET side stores categories, and the only symptom is a
score lower than it should be -- which reads like the model being bad at the task
rather than like a missing line in a string.

`docs/evals.md` section 1 is the source for the wording; `categories.py` is the
source for the list.
"""

from categorizer.categories import CATEGORIES, NO_PREDICTION
from categorizer.prompt import RESPONSE_SCHEMA, SYSTEM_PROMPT


def test_every_category_is_named_in_the_prompt():
    """Built from CATEGORIES rather than typed, so adding a twelfth is one edit in
    `categories.py` plus one line of description -- and forgetting the description
    is a KeyError at import rather than a silent omission here."""
    for category in CATEGORIES:
        assert f"- {category}:" in SYSTEM_PROMPT, category


def test_the_prompt_allows_abstention_explicitly():
    """#59 asks for this in as many words. It is not enough that the schema permits
    the sentinel -- a model that has not been told it may decline will guess, and a
    guess is stored as if it were true.

    **The assertion is on the instruction, not on the word**, and a mutation sweep is
    why. Replacing "answer \"unknown\"" with "pick the closest one" left the word
    `unknown` in the sentence *after* it -- the one explaining that it is not a
    category -- so a test asserting mere presence passed over a prompt that now tells
    the model to guess. That is the exact behaviour change this test exists to catch.

    Pinning wording usually makes a test fail on a reword that changes nothing, which
    #21 warns about for log messages. It is the right trade here: for a prompt, the
    wording *is* the behaviour, and there is no other observable to assert against
    without spending money on a model call.
    """
    assert f'answer "{NO_PREDICTION}"' in SYSTEM_PROMPT

    # The other half: nothing anywhere may tell it to guess instead.
    for guessing in ("pick the closest", "best guess", "always choose", "must choose"):
        assert guessing not in SYSTEM_PROMPT.lower(), guessing


def test_the_schema_is_the_vocabulary_plus_the_sentinel_and_nothing_else():
    """The enum is what makes a twelfth category unreachable through this route.

    Order matters here only in that it must match CATEGORIES -- if a future edit
    builds the enum from a set, this fails and says so, which is cheaper than
    discovering that two files disagree about what the eleven are.
    """
    assert RESPONSE_SCHEMA["properties"]["category"]["enum"] == [*CATEGORIES, NO_PREDICTION]
    assert RESPONSE_SCHEMA["additionalProperties"] is False


def test_the_prompt_does_not_name_a_category_that_does_not_exist():
    """`travel` and `education` were considered and folded in (docs/evals.md).

    A prompt that mentions either as a category would invite an answer the schema
    refuses and the scorer counts as a miss -- so this pins the two names most
    likely to come back. `travel` appears in the leisure description as a word, so
    the assertion is on the bulleted form only.
    """
    for absent in ("travel", "education", "income", "salary"):
        assert f"- {absent}:" not in SYSTEM_PROMPT, absent
