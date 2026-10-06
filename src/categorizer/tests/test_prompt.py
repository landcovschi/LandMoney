"""The prompt, checked against the vocabulary it is supposed to carry -- #59.

This file is short and it guards the failure mode that is hardest to see: a prompt
that omits a category produces a model that never answers it, and **nothing breaks**.
The service returns 200s, the .NET side stores categories, and the only symptom is a
score lower than it should be -- which reads like the model being bad at the task
rather than like a missing line in a string.

`docs/evals.md` section 1 is the source for the wording; `categories.py` is the
source for the list.
"""

import copy
import hashlib

from categorizer.categories import CATEGORIES, NO_PREDICTION
from categorizer.prompt import (
    _BOUNDARY_RULES,
    _WHAT_BELONGS,
    FINGERPRINT,
    RESPONSE_SCHEMA,
    SCHEMA_FINGERPRINT,
    SYSTEM_PROMPT,
    schema_fingerprint,
)


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


# --- what #97's guard hashes ---------------------------------------------------
#
# `evals/score.py --check-prompt` compares two digests against
# `evals/model-score.json`. Whether the comparison works is tested over there; what
# is tested here is that the digests are of what the model is sent, which is the
# half of #97 that decides whether the guard guards anything.


def test_the_prompt_digest_is_of_the_text_sent_and_not_of_the_file():
    """`test_anthropic_predictor.py` asserts that the system prompt sent is
    `system_prompt(False)`, which is SYSTEM_PROMPT; this closes the loop. A digest of
    the rendered string is why an edited comment passes the guard and an edited
    description fails it -- a digest of the file's bytes would do the reverse."""
    assert FINGERPRINT == hashlib.sha256(SYSTEM_PROMPT.encode("utf-8")).hexdigest()[:12]


def test_every_piece_the_prompt_is_assembled_from_reaches_the_digest():
    """#97: "a change assembled from three variables should" fail.

    The three are private on purpose -- nothing but this module should read them --
    and this is the one reader whose job is to prove they reach the hashed text.
    Verbatim, so that an edit to any one of them is an edit to SYSTEM_PROMPT and so
    to FINGERPRINT. A piece that was rendered somewhere the digest does not cover
    would pass the guard while changing what the model reads.
    """
    for description in _WHAT_BELONGS.values():
        assert description in SYSTEM_PROMPT, description
    for rule in _BOUNDARY_RULES:
        assert rule in SYSTEM_PROMPT, rule


def test_a_change_to_the_schema_alone_moves_its_digest():
    """The system prompt's digest deliberately does not cover the schema, so this is
    the only thing standing between `"required": []` and a green build."""
    loosened = {**RESPONSE_SCHEMA, "required": []}

    assert schema_fingerprint(loosened) != SCHEMA_FINGERPRINT


def test_the_same_schema_built_again_is_the_same_digest():
    """Determinism, or every run of the guard would be red."""
    assert schema_fingerprint(copy.deepcopy(RESPONSE_SCHEMA)) == SCHEMA_FINGERPRINT


def test_the_schema_digest_keeps_the_order_the_request_carries():
    """Not `sort_keys`, on purpose: a reorder is a change to what was sent, and a
    guard should ask for a number rather than decide on the model's behalf that the
    order cannot matter."""
    reordered = dict(reversed(list(RESPONSE_SCHEMA.items())))

    assert reordered == RESPONSE_SCHEMA
    assert schema_fingerprint(reordered) != SCHEMA_FINGERPRINT
