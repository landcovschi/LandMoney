"""A model behind the port -- #59, step 4 of slice 4.

This class satisfies `Predictor` and **imports nothing from `predictor.py`**. That
is `Protocol` being structural, and it is the whole reason the port was written
that way in #39: adding a second implementation needs no edit to the first, no
base class, and no registration -- `get_predictor` in `main.py` is the one line
that changes. The C# equivalent would need `: IPredictor` here and would make this
module depend on the one that defines it.

**What this file may not do**, because #39's `test_the_endpoint_answers_exactly_what_the_rules_do`
cannot catch it: change what `rules.py` sees. The normalisation below applies to
the *model's answer* and to nothing else. A well-meaning tidy-up of the incoming
description -- lower-casing it, stripping punctuation -- would improve this
predictor and silently move the baseline the improvement is measured against,
which is the exact mutation #39 caught by hand.
"""

import json
import logging
from decimal import Decimal
from typing import Any, Final, Mapping, Protocol

from categorizer.categories import KNOWN, NO_PREDICTION
from categorizer.contracts import CategorizeRequest, CategorizeResponse, Category, Source
from categorizer.prompt import RESPONSE_SCHEMA, SYSTEM_PROMPT

logger = logging.getLogger(__name__)

# Defaults, all overridable from the environment by `from_env` below.
DEFAULT_MODEL: Final[str] = "claude-opus-5"

# Six seconds, and the number is chosen against the .NET side rather than against
# this one. CategorizerClient allows the whole call eight (#59, and Program.cs
# carries the argument), so a request that is still running at six has already lost
# its caller -- finishing it would bill for an answer nobody will read. Giving up
# first also means the failure is logged *here*, where the model and the latency
# are known, instead of appearing on the .NET side as an unexplained timeout.
DEFAULT_TIMEOUT_SECONDS: Final[float] = 6.0

# Room for adaptive thinking, not for the answer. The answer is one word inside a
# constrained JSON object -- a handful of tokens -- but on Claude Opus 5 thinking
# is on by default and its tokens count against this ceiling, so the classification
# figure of ~256 that suits a non-thinking model would truncate mid-thought and
# cost a retry. The wall-clock ceiling is the timeout above; this is only what
# happens if the model runs long.
DEFAULT_MAX_TOKENS: Final[int] = 2048

# `low`, and it is a deliberate spend rather than a default. Effort controls
# thinking depth; this is a one-line classification against a rubric supplied in
# full, which is the shape `low` exists for, and it is the only lever here that
# trades quality against the latency the timeout above is fighting. #60 is where it
# gets measured -- raising it is one string, and the score says whether it was
# worth it.
#
# Note what is *not* here: `thinking: {"type": "disabled"}`. On Claude Opus 5 that
# has two documented failure modes -- a tool call written into visible text, and
# `<thinking>` tags leaking into the response -- and lowering effort gets the same
# saving without them.
DEFAULT_EFFORT: Final[str] = "low"


class _Messages(Protocol):
    def create(self, **kwargs: Any) -> Any: ...


class MessagesClient(Protocol):
    """The sliver of `anthropic.Anthropic` this adapter actually uses.

    Declared rather than importing the concrete client type for the same reason
    `Predictor` is a Protocol: it is what lets the tests hand in a stub with canned
    answers, so they never open a socket and never cost money. `anthropic.Anthropic`
    satisfies it without knowing this file exists.

    `Any` on the response is honest rather than lazy. Every field read below is read
    defensively -- see `_answer_from` -- because the guarantee that matters is the
    one the adapter enforces, not the one the SDK's types promise.
    """

    messages: _Messages


class AnthropicPredictor:
    """One transaction in, one category or an abstention out. Never raises."""

    def __init__(
        self,
        client: MessagesClient,
        model: str = DEFAULT_MODEL,
        max_tokens: int = DEFAULT_MAX_TOKENS,
        effort: str = DEFAULT_EFFORT,
    ) -> None:
        self._client = client
        self._model = model
        self._max_tokens = max_tokens
        self._effort = effort

    # Read-only, and they exist for #60 rather than for this class. A score is
    # only reproducible if what produced it is written down beside it, and the
    # alternative -- having `evals/score.py` re-read CATEGORIZER_MODEL and the
    # defaults itself -- is a second copy of `from_env`'s resolution, which is
    # the one place a scorer could report a model it did not run. Nothing here
    # is settable: the adapter is configured once, at construction.

    @property
    def model(self) -> str:
        return self._model

    @property
    def effort(self) -> str:
        return self._effort

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> "AnthropicPredictor":
        """Build the real client. The one place a network-capable object is made.

        `anthropic.Anthropic()` finds the key itself -- ANTHROPIC_API_KEY, or a
        profile written by `ant auth login` -- so the key is never named in this
        file and never passed as an argument that could reach a log or a traceback.
        Locally it comes from `.env`, which nothing in this repository may read out
        or print; there is no default and no placeholder.

        Imported here rather than at module scope so that `import
        categorizer.anthropic_predictor` costs nothing and needs no SDK present --
        which is what lets the tests below exercise every branch with a stub.
        """
        import anthropic

        # max_retries=0, against the SDK's default of 2, and it is arithmetic rather
        # than taste -- see the comment on the constructor call below.
        client = anthropic.Anthropic(
            timeout=float(env.get("CATEGORIZER_TIMEOUT_SECONDS", DEFAULT_TIMEOUT_SECONDS)),
            max_retries=0,
        )

        # **Measured rather than assumed, and it was assumed wrongly first.**
        # `anthropic.Anthropic()` with no credential anywhere does *not* raise: it
        # constructs, and the failure arrives at the first request. So a deployment
        # that selected the model and forgot the key starts cleanly, serves 200s,
        # and answers `category: null` for ever -- which is indistinguishable from a
        # model that declines every row, and would reach #60 as a very poor score
        # rather than as a misconfiguration.
        #
        # Not a raise, deliberately, and for the same reason `Categorizer:BaseUrl`
        # is a warning rather than a `?? throw` on the .NET side: a dependency this
        # system is designed to run without must not be able to stop it starting,
        # and every failure of this service is already a null category by design.
        # One line in the log at startup is what turns "silently free" into
        # "findable". It names no value and prints nothing from `.env`.
        if client.api_key is None and client.auth_token is None:
            logger.error(
                "CATEGORIZER_PREDICTOR is 'model' but no Anthropic credential was found. "
                "Every request will fail and answer with no category. Set ANTHROPIC_API_KEY."
            )

        return cls(
            # max_retries=0, against the SDK's default of 2, and it is arithmetic
            # rather than taste: retries multiply wall-clock, so the default would
            # let one call reach 3 x 6 = 18 seconds against a caller who gave up at
            # eight. A retry that finishes after its caller has gone is billed and
            # discarded. The fallback is already null on both sides of the wire, so
            # failing at the first attempt loses nothing that was going to be used.
            client,
            model=env.get("CATEGORIZER_MODEL", DEFAULT_MODEL),
            effort=env.get("CATEGORIZER_EFFORT", DEFAULT_EFFORT),
        )

    def categorize(self, request: CategorizeRequest) -> CategorizeResponse:
        """The `Predictor` port. Structural: nothing here names the Protocol."""
        return CategorizeResponse(
            category=self._category_for(request),
            # Named by the implementation that answered, never stamped from
            # configuration -- #59's rule, and the reason `Predictor` returns the
            # whole response rather than a bare category. A row saying `model`
            # because a setting said so, rather than because this code ran, is
            # exactly the lie the column was added to make impossible.
            #
            # `MODEL` even when the answer is None. An abstention by the model is
            # still the model answering, and it is the same shape `RulesPredictor`
            # produces when the rules decline. What it deliberately does not
            # distinguish is a decline from a failure -- see `_category_for`.
            source=Source.MODEL,
        )

    def _category_for(self, request: CategorizeRequest) -> Category | None:
        try:
            answer = self._ask(request)
        # **Broad on purpose, and this is the paragraph to read before narrowing
        # it.** This method sits on the path where a user's transaction is being
        # saved, and #39's promise is that categorising can never cost that row.
        # Anything raised here becomes a 500, which CategorizerClient already turns
        # into a null category -- so narrowing this to `anthropic.AnthropicError`
        # would not protect a single transaction. It would only move the failure one
        # process later, spend the round trip, and put a stack trace in the log of
        # the application rather than in the log of the thing that broke.
        #
        # `logger.exception` rather than `logger.warning`, so the traceback is kept:
        # the cost of catching broadly is that a bug in `_answer_from` looks exactly
        # like the model being unavailable, and the traceback is the only thing that
        # tells them apart.
        #
        # Deliberately *not* caught anywhere below this: nothing here takes a
        # cancellation token, so there is no equivalent of CategorizerClient's `when`
        # clause and no caller-cancellation to let through.
        except Exception:
            logger.exception("The model call failed; answering with no category.")
            return None

        if answer is None or answer == NO_PREDICTION:
            return None

        return Category(answer)

    def _ask(self, request: CategorizeRequest) -> str | None:
        message = self._client.messages.create(
            model=self._model,
            max_tokens=self._max_tokens,
            system=SYSTEM_PROMPT,
            messages=[{"role": "user", "content": _user_message(request)}],
            # Both keys live inside output_config: `format` constrains the answer to
            # the schema, `effort` controls how hard the model thinks about it.
            #
            # The schema is what makes "an answer outside the vocabulary" almost
            # unreachable rather than merely unlikely -- the enum is CATEGORIES plus
            # the sentinel, enforced by the API. `_answer_from` checks anyway, and
            # that is not belt-and-braces: this constraint is a property of one
            # route to one API, and the check is a property of this adapter.
            output_config={
                "format": {"type": "json_schema", "schema": RESPONSE_SCHEMA},
                "effort": self._effort,
            },
        )
        return _answer_from(message)


def _user_message(request: CategorizeRequest) -> str:
    """The transaction, and nothing about how to categorise it.

    Everything instructional is in the system prompt, which is stable across every
    request -- so this is the only part that varies, and it stays that way for the
    day #60 finds this worth caching. A prefix cache is invalidated by any byte
    change in the prefix, and moving one rule down here would invalidate it on every
    single call.
    """
    return (
        f"Description: {request.description}\n"
        f"Amount: {_plain(request.amount)}\n"
        f"Currency: {request.currency.upper()}"
    )


def _plain(amount: Decimal) -> str:
    """`12.34`, never `1.234E+1`.

    `str(Decimal)` uses scientific notation once the exponent moves far enough, and
    an amount typed as `1200.00` can arrive here as `Decimal("1.20E+3")` depending
    on how it was written on the wire. That is a legible number turned into one the
    model has to decode, on the one field the prompt says is there to separate a
    4.50 from a 450.
    """
    return f"{amount:f}"


def _answer_from(message: Any) -> str | None:
    """The model's category, normalised and checked, or None if it is unusable.

    **The normalisation the traps in #59 name**, and it applies here and nowhere
    else: `Groceries`, ` groceries ` and `GROCERIES ` are all `groceries`. What it
    does not do is map -- `food` is not a category and does not become one, because
    a synonym table here would be this adapter quietly answering a question the
    model was asked. It is out of vocabulary, and out of vocabulary is an abstention.

    Membership in `KNOWN` also settles the other trap without mentioning it: the
    .NET column is `MaxLength(100)`, and the longest of the eleven is thirteen
    characters. Checking a length here would be checking the wrong thing -- the
    vocabulary is the constraint, and it is far tighter.
    """
    text = next((block.text for block in message.content if block.type == "text"), None)
    if not text:
        # Reachable on `max_tokens`: the model thought past the ceiling and the
        # response carries thinking blocks and no text. Worth its own line in the
        # log, because the fix is a number in this file rather than anything about
        # the model or the network.
        logger.warning("The model answered nothing usable; answering with no category.")
        return None

    # `output_config.format` guarantees valid JSON, so this failing means the answer
    # did not come through that path -- which is exactly when the check is wanted.
    answer = json.loads(text).get("category")
    if not isinstance(answer, str):
        logger.warning("The model answered %r, which is not a category name.", answer)
        return None

    normalised = answer.strip().lower()

    if normalised == NO_PREDICTION:
        return NO_PREDICTION

    if normalised not in KNOWN:
        # A twelfth category is the failure the closed vocabulary exists to prevent,
        # and `Category(...)` in `_category_for` would raise on it -- so this is
        # what turns that refusal into a clean null instead of a 500, which is what
        # #59 asks for in as many words.
        logger.warning("The model answered %r, which is not in the vocabulary.", answer)
        return None

    return normalised
