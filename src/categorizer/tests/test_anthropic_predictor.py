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
import logging
from decimal import Decimal
from types import SimpleNamespace

import pytest

from categorizer.anthropic_predictor import (
    PRICE_INPUT_ENV,
    PRICE_OUTPUT_ENV,
    AnthropicPredictor,
    Prices,
    Usage,
    _prices_from,
)
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


def answering(
    category: str, *, with_thinking: bool = True, usage: object | None = None
) -> SimpleNamespace:
    blocks = [thinking_block()] if with_thinking else []
    message = SimpleNamespace(content=[*blocks, text_block(json.dumps({"category": category}))])

    # Absent unless a test asks for it, which is deliberate: every fixture here
    # predates #64, and a response carrying no usage at all is the case the reader
    # has to survive. `_usage_from` is written for exactly that.
    if usage is not None:
        message.usage = usage

    return message


def usage_of(input_tokens: int, output_tokens: int) -> SimpleNamespace:
    """The two fields this adapter reads off `message.usage`."""
    return SimpleNamespace(input_tokens=input_tokens, output_tokens=output_tokens)


def predictor_for(
    *answers: object, prices: Prices | None = None
) -> tuple[AnthropicPredictor, StubClient]:
    client = StubClient(*answers)
    return AnthropicPredictor(client, model="claude-opus-5", prices=prices), client


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


def test_the_predictor_reports_what_it_was_built_with():
    """The two properties #60 added, and they are not decoration.

    `evals/score.py` prints the model and the effort in the header above the
    score, because a number with no record of what produced it is not
    reproducible. The alternative was for the scorer to re-read CATEGORIZER_MODEL
    and these defaults itself, which is a second copy of `from_env` and the one
    way a report could name a model that did not answer.
    """
    predictor = AnthropicPredictor(StubClient(), model="claude-sonnet-5", effort="high")

    assert predictor.model == "claude-sonnet-5"
    assert predictor.effort == "high"


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


# --- what the call cost and what it did -- #64 --------------------------------
#
# `_prices_from` is imported by its underscored name on purpose. It is the one
# piece of #64's Python half that is a pure function of the environment, and the
# only other way to reach it is `from_env`, which constructs a real
# `anthropic.Anthropic` -- the one thing every test in this file exists to avoid.


def call_lines(caplog) -> list[str]:
    """The per-call lines, found by their prefix rather than by position."""
    return [
        record.getMessage()
        for record in caplog.records
        if record.getMessage().startswith("model_call ")
    ]


def test_the_tokens_the_response_reports_are_logged(caplog):
    """The fact, as opposed to the money. Tokens come from the response and stay
    true whatever anyone believes the price to be."""
    caplog.set_level(logging.INFO)
    predictor, _ = predictor_for(answering("groceries", usage=usage_of(1200, 8)))

    predictor.categorize(a_transaction())

    line = call_lines(caplog)[0]
    assert "input_tokens=1200" in line
    assert "output_tokens=8" in line


def test_a_response_that_reports_no_usage_is_unknown_and_not_zero():
    """Zero tokens and no idea how many are different claims, and only one of them
    survives being added up by something else."""
    assert Usage() == Usage(None, None)
    assert Prices(5.0, 25.0).cost_of(Usage()) is None


def test_the_cost_is_the_tokens_times_the_configured_price():
    """The published rate for claude-opus-5 on 2026-08-29: 5 USD and 25 USD per
    million. Written here as arguments rather than as a default in the module, so
    that the day the price moves this stays a statement about arithmetic instead of
    a stale figure pretending to be one."""
    assert Prices(5.0, 25.0).cost_of(Usage(1_000, 100)) == pytest.approx(0.0075)


def test_an_unpriced_predictor_still_reports_its_tokens(caplog):
    """Which is the whole reason there is no default price. A deployment that never
    set one is not left blind -- it is left without the multiplication."""
    caplog.set_level(logging.INFO)
    predictor, _ = predictor_for(answering("groceries", usage=usage_of(900, 7)))

    predictor.categorize(a_transaction())

    line = call_lines(caplog)[0]
    assert "input_tokens=900" in line
    assert "cost_usd=unpriced" in line


def test_a_priced_predictor_reports_what_the_call_cost(caplog):
    caplog.set_level(logging.INFO)
    predictor, _ = predictor_for(
        answering("groceries", usage=usage_of(1_000, 100)), prices=Prices(5.0, 25.0)
    )

    predictor.categorize(a_transaction())

    assert "cost_usd=0.007500" in call_lines(caplog)[0]


@pytest.mark.parametrize(
    ("answer", "outcome"),
    [
        (answering("groceries"), "answered"),
        (answering(NO_PREDICTION), "abstained"),
        (answering("takeaway"), "unusable"),
        (RuntimeError("connection reset"), "failed"),
    ],
)
def test_the_four_things_that_can_happen_are_four_words(answer, outcome, caplog):
    """**The split this half of #64 is for.** From the .NET side all four of these
    are `category: null` -- three arrive as a 200 and one as a 500 -- so only this
    process can say which. `abstained` is the model declining a row it was told it
    may decline; `unusable` is an answer thrown away for being outside the
    vocabulary, and it wants a look at the prompt rather than at the network."""
    caplog.set_level(logging.INFO)
    predictor, _ = predictor_for(answer)

    predictor.categorize(a_transaction())

    assert f"outcome={outcome}" in call_lines(caplog)[0]


def test_nothing_about_the_transaction_reaches_the_log(caplog):
    """#64's first trap, held at the place where it would actually be broken. A
    description is the user's own spending and a log line is where it would sit for
    ever, so the per-call line carries the model, the clock and the tokens, and
    nothing that says which purchase it was about."""
    caplog.set_level(logging.DEBUG)
    predictor, _ = predictor_for(answering("groceries", usage=usage_of(10, 2)))

    predictor.categorize(a_transaction(description="darwin chisinau", amount="847.19"))

    logged = " ".join(record.getMessage() for record in caplog.records)
    assert "darwin" not in logged
    assert "847.19" not in logged


# --- the prices, read from the environment ------------------------------------


def test_no_prices_configured_is_no_prices_and_no_complaint():
    assert _prices_from({}) is None


def test_both_prices_configured_is_a_price():
    assert _prices_from({PRICE_INPUT_ENV: "5", PRICE_OUTPUT_ENV: "25"}) == Prices(5.0, 25.0)


def test_half_a_price_is_no_price_and_says_so(caplog):
    """Somebody in the middle of doing this. Reporting nothing in silence would look
    exactly like never having tried."""
    caplog.set_level(logging.ERROR)

    assert _prices_from({PRICE_INPUT_ENV: "5"}) is None

    # Which error, and not merely that there was one. Found by mutation: deleting
    # the half-configured check entirely still passed this test, because `float("")`
    # then raises and the unparseable branch logs a message naming the same two
    # variables. An assertion that some error was logged cannot tell a rule from the
    # accident that happens to follow it.
    assert "Only one of" in caplog.text


def test_a_price_that_is_not_a_number_does_not_stop_the_service(caplog):
    """The opposite of how main.py treats an unrecognised CATEGORIZER_PREDICTOR, and
    the difference is what each mistake costs. A wrong predictor serves the baseline
    while the deployment believes a model is running; a wrong price leaves one field
    out of a log line. Taking the categorizer off the air over the second would be
    protecting an arithmetic convenience with an outage."""
    caplog.set_level(logging.ERROR)

    assert _prices_from({PRICE_INPUT_ENV: "five dollars", PRICE_OUTPUT_ENV: "25"}) is None
    assert caplog.records
