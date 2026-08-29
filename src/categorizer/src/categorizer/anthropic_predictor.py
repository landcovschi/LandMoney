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
import time
from decimal import Decimal
from typing import Any, Final, Mapping, NamedTuple, Protocol

from categorizer.cache import AnswerCache, CachedAnswer, cache_from_env, key_for
from categorizer.categories import KNOWN, NO_PREDICTION
from categorizer.contracts import CategorizeRequest, CategorizeResponse, Category, Source
from categorizer.prompt import FINGERPRINT, RESPONSE_SCHEMA, SYSTEM_PROMPT

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

# What one call cost, as far as this process can tell -- #64.
#
# **No default price, and that is the decision this whole section turns on.** The
# published rate for `claude-opus-5` on 2026-08-29 is 5.00 USD per million input
# tokens and 25.00 USD per million output tokens, and writing those two numbers
# into this file would produce a cost figure that stays confident and becomes
# wrong: a price changes without anything in this repository noticing, and a
# silently stale number in a log is worse than an absent one, because it is
# believed. So the prices are configuration, the figures above are what to put in
# it today, and with nothing configured the tokens are still logged -- they are the
# fact, the money is the multiplication.
PRICE_INPUT_ENV: Final[str] = "CATEGORIZER_PRICE_INPUT_PER_MTOK"
PRICE_OUTPUT_ENV: Final[str] = "CATEGORIZER_PRICE_OUTPUT_PER_MTOK"


class Usage(NamedTuple):
    """Tokens in and out, when the response says. None when it does not."""

    # Optional because this reads another library's object defensively, the same
    # way `_answer_from` reads the content blocks. A stub in a test carries no
    # usage; so does a response shape that changes. Neither is worth an exception on
    # the path where a transaction is being saved, and "unknown" is a thing a log
    # line can say.
    input_tokens: int | None = None
    output_tokens: int | None = None


class Prices(NamedTuple):
    """USD per million tokens, in and out."""

    input_per_mtok: float
    output_per_mtok: float

    def cost_of(self, usage: Usage) -> float | None:
        """What that call cost, or None if it cannot be worked out.

        Cache reads are deliberately not modelled: nothing here sends
        `cache_control`, so there are none, and a cache-aware calculation would be
        untested arithmetic guarding against a feature that does not exist. The day
        `_user_message`'s prefix-cache note is acted on, this figure becomes an
        overestimate and this is the docstring that says so.
        """
        if usage.input_tokens is None or usage.output_tokens is None:
            return None

        return (
            usage.input_tokens * self.input_per_mtok
            + usage.output_tokens * self.output_per_mtok
        ) / 1_000_000


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
        prices: Prices | None = None,
        cache: AnswerCache | None = None,
    ) -> None:
        self._client = client
        self._model = model
        self._max_tokens = max_tokens
        self._effort = effort
        # None means every call is billed, and that is the default rather than a
        # degraded state -- #65 adds a cache to the *model* path and nothing else.
        # An argument for the same reason the client is one: a test that wanted a
        # cache has to say so, so no test can reach a real Redis by forgetting.
        self._cache = cache
        # An argument rather than something read from the environment in here, for
        # the same reason the client is: a test names the configuration it is
        # testing instead of mutating the process. None means unpriced, which is
        # the state a deployment that never set a price is in.
        self._prices = prices

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
            prices=_prices_from(env),
            # The only place a cache is ever built -- #65. It is inside the model
            # adapter's factory rather than in `main.py` so that the rules path
            # cannot reach Redis even by a later edit: `RulesPredictor` is
            # constructed on a branch that never runs this line.
            cache=cache_from_env(env),
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
        # #64. Everything from here to the end of this method is about being able to
        # say afterwards what happened, and the four outcomes below are the four
        # this process can tell apart -- which is one more than the .NET side can
        # see, since `unusable` and `abstained` are the same `category: null` on the
        # wire.
        #
        # **This service is authoritative for what the model did**, and the .NET
        # client is authoritative for what the user got. #64 asks for that to be
        # decided rather than discovered, and the split follows from what each side
        # can observe: a call that answers at seven seconds is billed, counted here,
        # and already abandoned over there, while a request that never arrives is
        # counted there and unknowable here. So "how often does the model answer" is
        # this file's number, and "how often did a save get a category" is
        # CategorizerClient's. Neither is a correction of the other.
        # **The exact string the model is shown, built once and used twice** -- it
        # goes on the wire and it is what the cache key is a digest of. That is
        # #65's sharpest trap answered structurally rather than by remembering:
        # there is no second construction of this text to normalise differently,
        # so the key cannot drift from the input even by a well-meaning edit.
        shown = _user_message(request)

        # Built whether or not there is a cache, which is a sha256 of about a hundred
        # bytes -- a microsecond, against a call that takes two seconds. The
        # alternative is an Optional key and a second `is not None` at both of the
        # two places below, to save an amount of work this method cannot measure.
        key = key_for(
            model=self._model,
            effort=self._effort,
                # The prompt's own digest, from prompt.py, which is also what
                # `evals/score.py` prints above a score. An edited prompt therefore
                # changes every key in the same commit that changes the label on the
                # number -- so the first run after an edit measures the new prompt
                # rather than replaying answers to the old one.
            prompt_fingerprint=FINGERPRINT,
            user_message=shown,
        )

        if self._cache is not None:
            cached = self._cache.get(key)
            if cached is not None:
                # No `model_call` line here, and that is deliberate: that line means
                # a call was made, and on a hit there was none. The cache logs its
                # own line with the hit rate and what this hit did not spend, so the
                # two never have to be subtracted from each other to get either
                # number. `self.stats` is where a hit is counted -- see cache.py.
                return _category_of(cached.answer)

        started = time.perf_counter()
        usage = Usage()

        try:
            answer, usage = self._ask(shown)
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
            self._log_call("failed", started, usage)
            return None

        # Three separate lines where the code above had one condition, and the
        # distinction is the point rather than the tidiness. `abstained` is the model
        # declining a row it was told it may decline; `unusable` is the model
        # answering something this adapter threw away -- a word outside the
        # vocabulary, an empty response, a body that is not JSON. Both are a null
        # category and both count as a miss in #60's score, and they want different
        # reactions: one is the prompt working, the other is the prompt or the
        # schema needing a look.
        if answer is None:
            self._log_call("unusable", started, usage)
            return None

        # Stored here rather than at either of the two returns below, because both
        # of them are the model having answered and both cost the same money.
        # **An abstention is an answer**: asking again would buy the same `unknown`
        # at the same price, and the row is a miss in #60's score either way.
        #
        # What is deliberately never stored is above this line: `failed` -- the call
        # raised -- and `unusable` -- the answer was thrown away. Neither is
        # something the model said, and caching either would freeze a network blip
        # or a bad response for the whole TTL, turning a transient failure into a
        # month of them. A miss costs one call; a poisoned entry costs every call.
        if self._cache is not None:
            self._cache.put(
                key,
                CachedAnswer(
                    answer,
                    self._model,
                    usage.input_tokens,
                    usage.output_tokens,
                    # What it cost *when it was made*, not what the same tokens
                    # would cost today. A price change must not rewrite what a
                    # past call was billed -- the same reasoning that keeps the
                    # price out of the code in #64.
                    self._cost_of(usage),
                ),
            )

        if answer == NO_PREDICTION:
            self._log_call("abstained", started, usage)
            return None

        self._log_call("answered", started, usage)
        return Category(answer)

    def _log_call(self, outcome: str, started: float, usage: Usage) -> None:
        """One line per call: what happened, how long, how many tokens, what it cost.

        `key=value` rather than a sentence, because this is the line something will
        eventually parse. It is deliberately not JSON: uvicorn owns the logging
        configuration here, and a `dictConfig` that reformats every line this
        service and its server write is a bigger change than #64 asks for -- the
        .NET half took the JSON console because its fields reach Log Analytics as
        rows, and this half has no such consumer today.

        **Nothing about the transaction is in it.** #64's first trap is about metric
        cardinality and the same rule holds harder here: the description is the
        user's own spending, and a log line is where it would sit for ever.
        """
        cost = self._cost_of(usage)

        logger.info(
            "model_call outcome=%s model=%s effort=%s elapsed_ms=%.0f "
            "input_tokens=%s output_tokens=%s cost_usd=%s",
            outcome,
            self._model,
            self._effort,
            (time.perf_counter() - started) * 1000,
            # "unknown" rather than 0: a response that did not report its usage and a
            # call that used no tokens are different things, and a zero would quietly
            # become a zero in whatever adds these up.
            _or_unknown(usage.input_tokens),
            _or_unknown(usage.output_tokens),
            "unpriced" if cost is None else f"{cost:.6f}",
        )

    def _cost_of(self, usage: Usage) -> float | None:
        """What one call cost, or None when nothing here can say.

        One expression, read by the log line and by the cache entry, so a cached
        saving and a logged charge can never be two different arithmetics.
        """
        return self._prices.cost_of(usage) if self._prices else None

    def _ask(self, shown: str) -> tuple[str | None, Usage]:
        message = self._client.messages.create(
            model=self._model,
            max_tokens=self._max_tokens,
            system=SYSTEM_PROMPT,
            messages=[{"role": "user", "content": shown}],
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
        # The usage travels back beside the answer rather than being read from a
        # field on `self`, so that two calls cannot interleave and report each
        # other's tokens. FastAPI runs this handler on a worker thread (`def`, not
        # `async def` -- see main.py), so that is a real race and not a theoretical
        # one.
        return _answer_from(message), _usage_from(message)


def _category_of(answer: str) -> Category | None:
    """A stored or freshly-given answer as the response's field.

    The sentinel stops here exactly as it does in `RulesPredictor`: `unknown` is
    not one of the eleven and must never reach the .NET column. `Category(...)`
    cannot raise on a cached value, because `CachedAnswer.from_json` refuses
    anything outside the vocabulary before it gets this far.
    """
    return None if answer == NO_PREDICTION else Category(answer)


def _prices_from(env: Mapping[str, str]) -> Prices | None:
    """The two prices, or None -- which means the log reports tokens and no money.

    **An unparseable price does not stop the process**, which is the opposite of how
    `main.py` treats an unrecognised `CATEGORIZER_PREDICTOR`, and the difference is
    what each mistake costs. There, the wrong value serves the rules while the
    deployment believes a model is running, and #60 would record a baseline under a
    model's name. Here the worst case is a missing figure in a diagnostic line: a
    price affects nothing this service does, so refusing to start over one would
    take a categorizer off the air to protect an arithmetic convenience.

    Half-configured is the case worth a line of its own. One price set and the other
    absent is somebody in the middle of doing this, and silently reporting nothing
    would look identical to never having tried.
    """
    raw_input = env.get(PRICE_INPUT_ENV, "").strip()
    raw_output = env.get(PRICE_OUTPUT_ENV, "").strip()

    if not raw_input and not raw_output:
        return None

    if not raw_input or not raw_output:
        logger.error(
            "Only one of %s and %s is set, so no cost will be reported. Both are needed.",
            PRICE_INPUT_ENV,
            PRICE_OUTPUT_ENV,
        )
        return None

    try:
        return Prices(float(raw_input), float(raw_output))
    except ValueError:
        # The values are prices rather than credentials, so naming them is what
        # makes this fixable; there is nothing here that `.env` would not want read
        # aloud.
        logger.error(
            "%s=%r and %s=%r are not both numbers, so no cost will be reported.",
            PRICE_INPUT_ENV,
            raw_input,
            PRICE_OUTPUT_ENV,
            raw_output,
        )
        return None


def _usage_from(message: Any) -> Usage:
    """Tokens in and out, read the way `_answer_from` reads the content blocks.

    Defensively, and for the same reason: this is another library's object arriving
    over a network, and a missing attribute here would raise on the path where a
    user's transaction is being saved -- turning an accounting detail into a failed
    guess. Anything unreadable is None, which the log line prints as `unknown`.
    """
    usage = getattr(message, "usage", None)

    return Usage(_as_tokens(getattr(usage, "input_tokens", None)),
                 _as_tokens(getattr(usage, "output_tokens", None)))


def _as_tokens(value: Any) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def _or_unknown(tokens: int | None) -> object:
    return "unknown" if tokens is None else tokens


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
