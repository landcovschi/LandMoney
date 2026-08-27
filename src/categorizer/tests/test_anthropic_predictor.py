"""The model adapter, driven entirely by canned responses -- #59.

**Nothing here opens a socket and nothing here costs money.** That is the property
the whole file exists to hold, and #59 names why it needs holding: a test that
spends money passes, so the failure is silent and arrives as a bill. The stub below
is what makes it structural rather than a rule people remember -- `AnthropicPredictor`
takes its client as a constructor argument, so a test that forgot to pass one would
fail to construct rather than quietly reach the network.

`StubClient` inherits nothing and imports nothing from `anthropic_predictor`, the
same way `FakePredictor` in `test_api.py` inherits nothing from `predictor`. Both
are `Protocol` being structural; here it also means these tests run with the SDK
uninstalled.

The awkward cases #59 asks for are all below: an answer outside the vocabulary, an
empty answer, a very long answer, and an exception from the client.
"""

import json
from decimal import Decimal
from types import SimpleNamespace

import pytest

from categorizer.anthropic_predictor import AnthropicPredictor
from categorizer.categories import CATEGORIES, NO_PREDICTION
from categorizer.contracts import CategorizeRequest, Category, Source

# --- the fake ----------------------------------------------------------------


class StubMessages:
    def __init__(self, answers: list[object]) -> None:
        self._answers = list(answers)
        self.calls: list[dict[str, object]] = []

    def create(self, **kwargs: object) -> object:
        self.calls.append(kwargs)
        answer = self._answers.pop(0)
        if isinstance(answer, BaseException):
            raise answer
        return answer


class StubClient:
    """Shaped like `anthropic.Anthropic` in the one place this adapter touches it."""

    def __init__(self, *answers: object) -> None:
        self.messages = StubMessages(list(answers))


def text_block(text: str) -> SimpleNamespace:
    return SimpleNamespace(type="text", text=text)


def thinking_block() -> SimpleNamespace:
    """What Claude Opus 5 puts before the answer, since thinking is on by default.

    Present in these fixtures rather than assumed away: an adapter that read
    `content[0].text` would pass every test built from a text block alone and fail
    on the first real call, which is the worst place to find out.
    """
    return SimpleNamespace(type="thinking", thinking="")


def answering(category: str, *, with_thinking: bool = True) -> SimpleNamespace:
    blocks = [thinking_block()] if with_thinking else []
    return SimpleNamespace(content=[*blocks, text_block(json.dumps({"category": category}))])


def predictor_for(*answers: object) -> tuple[AnthropicPredictor, StubClient]:
    client = StubClient(*answers)
    return AnthropicPredictor(client, model="claude-opus-5"), client


def a_transaction(description: str = "linella chisinau", amount: str = "312.40") -> CategorizeRequest:
    return CategorizeRequest(description=description, amount=Decimal(amount), currency="MDL")


# --- the normal answers -------------------------------------------------------


@pytest.mark.parametrize("category", CATEGORIES)
def test_every_category_in_the_vocabulary_survives_the_round_trip(category: str):
    """All eleven, so a normalisation that mangled one would be caught rather than
    the one category somebody thought to write a test for."""
    predictor, _ = predictor_for(answering(category))

    answer = predictor.categorize(a_transaction())

    assert answer.category == Category(category)
    assert answer.source == Source.MODEL


def test_the_model_declining_is_a_null_category_and_not_the_sentinel():
    """`unknown` is what `rules.py` returns too, and it stops at this boundary the
    same way -- serving it would put a twelfth value into the .NET column."""
    predictor, _ = predictor_for(answering(NO_PREDICTION))

    answer = predictor.categorize(a_transaction("something nobody can place"))

    assert answer.category is None
    assert answer.source == Source.MODEL


def test_the_source_is_model_even_when_there_is_no_category():
    """An abstention by the model is still the model answering.

    The alternative -- leaving `source` unset when there is nothing to report -- would
    make `CategorizeResponse.source` optional for one caller's benefit, and the .NET
    side already refuses a category with no source. There is no category here, so
    there is nothing for it to refuse.
    """
    predictor, _ = predictor_for(answering(NO_PREDICTION))

    assert predictor.categorize(a_transaction()).source == Source.MODEL


# --- normalisation, and the line it may not cross -----------------------------


@pytest.mark.parametrize("answered", ["Groceries", " groceries ", "GROCERIES", "\tGroceries\n"])
def test_case_and_whitespace_are_normalised(answered: str):
    """#59's first trap. The model was told to answer exactly as written and this is
    what happens when it does not quite."""
    predictor, _ = predictor_for(answering(answered))

    assert predictor.categorize(a_transaction()).category == Category.GROCERIES


def test_a_synonym_is_not_mapped_and_is_an_abstention():
    """The other half of the same trap, and the more important half.

    `food` is a reasonable thing for a model to say and it is not a category.
    Mapping it here would be the adapter answering a question the model was asked,
    and every such mapping is a rule that improves this predictor's score without
    appearing in the prompt that is supposedly being measured.
    """
    predictor, _ = predictor_for(answering("food"))

    assert predictor.categorize(a_transaction()).category is None


def test_the_description_reaches_the_model_exactly_as_it_arrived():
    """The mutation #39 caught by hand, guarded here.

    Normalising the *input* -- lower-casing it, replacing hyphens -- would improve
    this predictor and leave the rules baseline describing code that no longer runs
    beside it. The normalisation in this adapter applies to the model's answer and
    to nothing else.
    """
    typed = "  Coffee-Beans  at  LINELLA  "
    predictor, client = predictor_for(answering("groceries"))

    predictor.categorize(a_transaction(typed))

    assert typed in client.messages.calls[0]["messages"][0]["content"]


# --- the awkward answers #59 asks for -----------------------------------------


def test_an_answer_outside_the_vocabulary_is_null_and_not_a_crash():
    """`Category(...)` would raise on this, which reaches the .NET side as a 500.

    The schema makes it nearly unreachable through the real API -- the enum is
    CATEGORIES plus the sentinel -- and the check exists anyway, because that
    constraint is a property of one route and this is a property of the adapter.
    """
    predictor, _ = predictor_for(answering("takeaway"))

    assert predictor.categorize(a_transaction()).category is None


@pytest.mark.parametrize(
    "content",
    [
        [],                                        # nothing at all
        [thinking_block()],                        # thought past max_tokens, no text left
        [text_block("")],                          # an empty text block
    ],
)
def test_an_empty_answer_is_null(content: list[object]):
    predictor, _ = predictor_for(SimpleNamespace(content=content))

    assert predictor.categorize(a_transaction()).category is None


def test_a_very_long_answer_is_null():
    """Ten thousand characters where a word was expected.

    It is null for the right reason: not a length check, but membership in a closed
    vocabulary whose longest member is thirteen characters. #59's second trap says
    the adapter should never be the thing that discovers the .NET column is
    `MaxLength(100)` -- and it never can be, because nothing over thirteen leaves
    this method.
    """
    predictor, _ = predictor_for(answering("groceries " * 1000))

    assert predictor.categorize(a_transaction()).category is None


def test_an_answer_that_is_not_json_is_null():
    """Only reachable if the response did not come through `output_config.format`,
    which is exactly when the check is wanted."""
    predictor, _ = predictor_for(SimpleNamespace(content=[text_block("groceries, obviously")]))

    assert predictor.categorize(a_transaction()).category is None


@pytest.mark.parametrize("answered", [None, 7, ["groceries"], {"name": "groceries"}])
def test_json_whose_category_is_not_a_string_is_null(answered: object):
    predictor, _ = predictor_for(
        SimpleNamespace(content=[text_block(json.dumps({"category": answered}))])
    )

    assert predictor.categorize(a_transaction()).category is None


def test_an_exception_from_the_client_is_null_rather_than_a_raise():
    """The promise this class shares with CategorizerClient: a failed guess may
    never cost the user's transaction. Anything raised here becomes a 500, which
    the .NET side turns into null anyway -- one round trip and one alarming
    traceback later, in the wrong process's log."""
    predictor, _ = predictor_for(RuntimeError("connection reset by peer"))

    answer = predictor.categorize(a_transaction())

    assert answer.category is None
    assert answer.source == Source.MODEL


def test_a_failure_and_a_decline_are_not_distinguishable_from_outside():
    """Said out loud because it is a cost rather than a feature.

    `{category: null, source: "model"}` is what both produce, so a run of failures
    and a run of declines look identical to the caller and identical in the
    database. The log is the only place they differ -- `logger.exception` with a
    traceback against no line at all. #60 measures a score, and a score cannot tell
    these apart either.
    """
    declined, _ = predictor_for(answering(NO_PREDICTION))
    failed, _ = predictor_for(RuntimeError("boom"))

    assert declined.categorize(a_transaction()) == failed.categorize(a_transaction())


# --- what goes out ------------------------------------------------------------


def test_the_request_carries_the_prompt_the_schema_and_the_effort():
    """Asserted rather than assumed, for the reason CategorizerClientTests gives
    about the wire format: every one of these being wrong fails as "the model is
    bad at this" rather than as an error.

    A dropped schema means unconstrained prose and every answer null; a dropped
    system prompt means a model that has never seen the eleven categories; a
    dropped `effort` is a silent bill.
    """
    from categorizer.prompt import RESPONSE_SCHEMA, SYSTEM_PROMPT

    predictor, client = predictor_for(answering("groceries"))

    predictor.categorize(a_transaction())
    sent = client.messages.calls[0]

    assert sent["model"] == "claude-opus-5"
    assert sent["system"] == SYSTEM_PROMPT
    assert sent["output_config"]["format"]["schema"] == RESPONSE_SCHEMA
    assert sent["output_config"]["effort"] == "low"
    assert sent["messages"] == [{"role": "user", "content": sent["messages"][0]["content"]}]


@pytest.mark.parametrize(
    ("amount", "expected"),
    [
        ("12.34", "12.34"),
        ("1200.00", "1200.00"),
        ("0.01", "0.01"),
        # **The row that makes this test able to fail.** The first three pass
        # whether the amount is formatted or merely `str()`-ed, because `str` and
        # `f` agree on a Decimal whose exponent is already negative -- so without
        # this row the test asserted nothing about the thing it is named for, and a
        # mutation sweep said so.
        #
        # `str(Decimal("1E+3"))` is `'1E+3'`; the `f` format is `'1000'`. Worth
        # knowing exactly how reachable that is, since it decides whether the guard
        # is justified at all: **not over HTTP** -- pydantic normalises a JSON `1E+3`
        # to `1000` before this code sees it -- but **yes in-process**, which is how
        # `evals/score.py` will drive this predictor in #60, building Decimals from
        # CSV text rather than posting JSON. So the guard protects the eval path
        # rather than the serving path, and the eval path is the one that produces
        # the number.
        ("1E+3", "1000"),
    ],
)
def test_the_amount_is_written_plainly_and_never_in_scientific_notation(amount: str, expected: str):
    """The prompt tells the model the amount is there to separate a 4.50 from a 450,
    so handing it `1E+3` undoes the one thing it was included for."""
    predictor, client = predictor_for(answering("groceries"))

    predictor.categorize(a_transaction(amount=amount))

    assert f"Amount: {expected}" in client.messages.calls[0]["messages"][0]["content"]


def test_the_currency_is_upper_cased():
    """The .NET side uppercases before storing and before asking, so this only
    matters for a caller that is not it -- and the prompt reads better than the
    contract's `^[A-Za-z]{3}$` allows for."""
    predictor, client = predictor_for(answering("groceries"))

    predictor.categorize(
        CategorizeRequest(description="linella", amount=Decimal("10.00"), currency="mdl")
    )

    assert "Currency: MDL" in client.messages.calls[0]["messages"][0]["content"]


def test_one_transaction_is_one_call():
    """No retry loop, no second opinion, no batching. #60 costs one call per row and
    the arithmetic for a 53-row eval run should hold."""
    predictor, client = predictor_for(answering("groceries"))

    predictor.categorize(a_transaction())

    assert len(client.messages.calls) == 1
