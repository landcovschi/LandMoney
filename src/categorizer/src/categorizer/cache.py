"""The answer cache -- #65, and slice 5's first line: identical input must not be
billed twice.

`CLAUDE.md` says a database gets added when it has a job. This is that day and not
a day earlier: there was nothing to cache while the answer came from 109 substrings
in memory, and there still is not -- **nothing in this module is reachable from the
rules path.** `RulesPredictor` does not import it, `main.py` builds it nowhere, and
the only construction is inside `AnthropicPredictor.from_env`. A cache in front of a
free in-memory scan would make it slower and would be a second thing that can be
down.

**The key is the sharpest thing here, and the trap #65 names is the one this module
is shaped around.** Any normalisation applied to build a key -- lower-casing,
trimming, collapsing spaces -- would be a rule that exists in the cache path and
nowhere else, which is the same class of drift as the mutation caught by hand in
#39: it looks like an improvement and it silently makes the recorded score a number
about code that no longer runs. So there is none. `key_for` hashes the exact string
the model is shown, byte for byte, and `anthropic_predictor.py` builds that string
once and uses it for both -- the same value goes on the wire and into the digest, so
they cannot drift even by accident.

The other three parts of the key are what stop yesterday's answers being served
forever: the model id, the effort, and the prompt fingerprint. Edit `prompt.py` and
every key changes, so the next eval run measures the new prompt rather than a
replay of the old one.

**Redis being down means "call the model", never "no category".** Every method here
swallows what the client raises and answers as a miss. That is the same promise
`AnthropicPredictor` and `CategorizerClient` already make one layer out, and this
module is the third place in the chain where a dependency's absence must not become
a failure -- so, per #64, it is also the third place where that absence has to be
counted or nobody will ever know it happened.
"""

import hashlib
import json
import logging
import threading
import time
from typing import Any, Callable, Final, Mapping, NamedTuple, Protocol

from categorizer.categories import KNOWN, NO_PREDICTION

logger = logging.getLogger(__name__)

REDIS_URL_ENV: Final[str] = "CATEGORIZER_REDIS_URL"
TTL_ENV: Final[str] = "CATEGORIZER_CACHE_TTL_SECONDS"

# Thirty days. The key already carries the model, the effort and the prompt, so a
# changed answer is a changed key and staleness is not what this bounds -- what it
# bounds is a Redis that grows forever holding entries for descriptions nobody will
# type again. Thirty days is long enough that a monthly bill's worth of repeated
# merchants is still warm.
DEFAULT_TTL_SECONDS: Final[int] = 30 * 24 * 60 * 60

# `v1` is the one lever this file has that `prompt.py` does not: it invalidates
# every key at once, for the day the *value* format changes rather than the answer.
KEY_PREFIX: Final[str] = "landmoney:categorizer:v1:"

# Deliberately far below the six seconds the model call gets. A Redis that accepts
# a connection and then stops answering must not eat a budget the .NET side already
# capped at eight seconds (#59) -- the cache exists to make the call cheaper, so it
# may never be what makes it slower. Half a second on a service on the same private
# network is generous by two orders of magnitude.
CONNECT_TIMEOUT_SECONDS: Final[float] = 0.5
SOCKET_TIMEOUT_SECONDS: Final[float] = 0.5

# How long the cache stops asking after a failure -- **measured rather than
# invented, and the measurement is the reason this exists at all.** With the redis
# container stopped, a lookup and a write each pay the connect timeout in full: a
# stopped container leaves the SYN unanswered rather than refusing it, which is
# #39's finding about the categorizer arriving one service along. That is
# 1055 ms added to a save that was going to be 2 s, on the path where a user's
# transaction is being written, for every save until somebody notices.
#
# So after a failure the next thirty seconds of lookups are answered "no" without
# touching the socket, which costs 0 ms and one log line each. The price is that a
# Redis which comes back is not used for up to thirty seconds -- misses, never
# wrong answers -- and that is a far better trade than a second of latency per save
# while it is away.
#
# Not a circuit breaker library and deliberately not a real one: there is no
# half-open state and no failure threshold, because one failure is enough evidence
# for something whose whole job is to be faster than the alternative.
DOWN_FOR_SECONDS: Final[float] = 30.0


class CachedAnswer(NamedTuple):
    """What one model call produced, and what it cost -- #65's second bullet.

    The cost travels *with* the answer rather than only into the log, and that is
    the point of storing it: on a hit the saving is a number this process can name
    rather than an estimate somebody works out later from a price list. It is what
    the call cost when it was made, so a later price change does not rewrite
    history -- which is the same reasoning that keeps the price out of the code in
    #64.

    **The description is not in here, and neither is anything else about the
    transaction.** The key is a digest and the value is an answer, so a dump of this
    Redis shows what was categorised as what and never what was bought. #64 made
    that rule for log lines; a cache is where it would be far easier to break, since
    storing the description would make the entries readable by hand.
    """

    # A category name, or NO_PREDICTION. Never None: "the model declined" is an
    # answer and worth caching, where "the call failed" is not an answer at all and
    # never reaches here -- see `AnthropicPredictor._category_for`.
    answer: str
    model: str
    input_tokens: int | None
    output_tokens: int | None
    cost_usd: float | None

    def to_json(self) -> str:
        return json.dumps(self._asdict(), separators=(",", ":"))

    @classmethod
    def from_json(cls, raw: str) -> "CachedAnswer | None":
        """The stored entry, or None if it cannot be trusted -- which reads as a miss.

        Validated rather than deserialised, and the check that matters is
        membership in `KNOWN`. Everything else in this repository refuses to let a
        twelfth category reach `transactions.category`; a cache that served one back
        would be a way round all of it, and Redis is the one store here that
        something other than this code can write to.
        """
        try:
            fields = json.loads(raw)
            answer = fields["answer"]
        except (ValueError, TypeError, KeyError):
            return None

        if not isinstance(answer, str) or (answer not in KNOWN and answer != NO_PREDICTION):
            logger.warning("A cached answer was %r, which is not a category. Ignoring it.", answer)
            return None

        return cls(
            answer,
            str(fields.get("model", "")),
            _as_int(fields.get("input_tokens")),
            _as_int(fields.get("output_tokens")),
            _as_float(fields.get("cost_usd")),
        )


def key_for(*, model: str, effort: str, prompt_fingerprint: str, user_message: str) -> str:
    """The cache key. **Nothing here normalises anything** -- see the module docstring.

    The four parts are joined by newlines and `user_message` is last, which is what
    makes the framing unambiguous: it is the only one of the four that can itself
    contain a newline, so no two different tuples can produce the same joined
    string. A model id or an effort with a newline in it is not a thing that exists.

    Hashed rather than stored plainly for two reasons of different weight. The small
    one is that a Redis key holding a description is a description sitting in
    another process's memory. The large one is that the digest is fixed-length, so a
    500-character description -- the contract's ceiling -- cannot produce a key
    Redis has an opinion about.
    """
    material = "\n".join([model, effort, prompt_fingerprint, user_message])
    return KEY_PREFIX + hashlib.sha256(material.encode("utf-8")).hexdigest()


class AnswerCache(Protocol):
    """The port, the same way `Predictor` is one: structural, and never implemented
    by inheriting from it.

    Two methods and no `delete`, no `clear` and no `close`. Nothing in this service
    invalidates an entry -- the key changes instead, which is what the model id and
    the prompt fingerprint are in it for -- and adding a method here to be tidy
    would be a method the fake in the tests has to grow for nobody.
    """

    def get(self, key: str) -> CachedAnswer | None: ...

    def put(self, key: str, answer: CachedAnswer) -> None: ...


class CacheStats:
    """Hits, misses, failures, and the money the hits did not spend -- #65's third
    bullet, because a cache nobody measured is a cache nobody knows is working.

    In-process and therefore short-lived, which is the same shape #64's .NET tally
    has and for the same reason: this container scales to zero, so any total here
    describes at most one replica's afternoon. The durable record is the log line,
    which carries the running totals on every call -- so the last line of a replica's
    life is its whole story, and a reader who wants the hit rate over a week adds up
    lines rather than asking a process that has since died.

    Locked because Starlette dispatches this service's handlers to a worker thread
    pool (`def`, not `async def` -- see `main.py`), so two calls really can land in
    here at once. `+=` on an int is not atomic across threads.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.hits = 0
        self.misses = 0
        # Kept apart from misses on purpose. A miss is the cache working and a
        # failure is the cache being down, and both end in the same model call --
        # so without this line the one state worth an alarm looks exactly like an
        # ordinary cold key.
        self.failures = 0
        self.saved_usd = 0.0

    def hit(self, cost_usd: float | None) -> None:
        with self._lock:
            self.hits += 1
            # None when the stored call reported no usage, or when no price was
            # configured at the time. The hit still counts; only the money is
            # unknown, and adding a zero would quietly report a saving of nothing.
            if cost_usd is not None:
                self.saved_usd += cost_usd

    def miss(self) -> None:
        with self._lock:
            self.misses += 1

    def failure(self) -> None:
        with self._lock:
            self.failures += 1

    def line(self) -> str:
        """The totals as they appear in every cache log line."""
        with self._lock:
            looked_up = self.hits + self.misses + self.failures
            rate = 100.0 * self.hits / looked_up if looked_up else 0.0
            return (
                f"hits={self.hits} misses={self.misses} failures={self.failures} "
                f"hit_rate={rate:.1f}% saved_usd={self.saved_usd:.6f}"
            )


class _RedisClient(Protocol):
    """The sliver of `redis.Redis` this cache uses, declared for the same reason
    `MessagesClient` is: so the tests hand in a stub and never open a socket."""

    def get(self, name: str) -> Any: ...

    def set(self, name: str, value: str, ex: int | None = None) -> Any: ...


class RedisCache:
    """`AnswerCache` over Redis. Never raises, and says what it did.

    The logging lives here rather than in the adapter because this is the only
    object that knows all three of what happened, how long it took, and what the
    stored entry cost when it was made. The adapter's own `model_call` line is
    deliberately not emitted on a hit: that line means a call was made, and on a hit
    there was none. One line per lookup, and the two kinds never have to be
    subtracted from each other.
    """

    def __init__(
        self,
        client: _RedisClient,
        ttl_seconds: int = DEFAULT_TTL_SECONDS,
        stats: CacheStats | None = None,
        down_for_seconds: float = DOWN_FOR_SECONDS,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._client = client
        self._ttl_seconds = ttl_seconds
        self.stats = stats or CacheStats()
        self._down_for_seconds = down_for_seconds
        # An argument rather than `Microsoft.Extensions.TimeProvider.Testing`'s
        # Python equivalent, for the reason CLAUDE.md gives for keeping that package
        # out of the .NET side: a fake clock is six lines. `monotonic` and not
        # `time()`, because this measures an interval and the wall clock can move.
        self._clock = clock
        self._silent_until = 0.0

    def get(self, key: str) -> CachedAnswer | None:
        started = time.perf_counter()

        if self._is_down():
            # Counted, so a hit rate of zero is never a mystery, and logged, so
            # there is one line per lookup however this ends. No traceback: the
            # first failure already carries it, and repeating it every save for
            # thirty seconds would bury the line that says what broke.
            self.stats.failure()
            self._log("down", started, None)
            return None

        try:
            raw = self._client.get(key)
        # Broad, and for the same reason `AnthropicPredictor._category_for` is: this
        # sits on the path where a user's transaction is being saved. Catching
        # `redis.RedisError` would leave a DNS failure, a bad URL and a stub that
        # raises something unexpected as three ways to lose a transaction to a cache
        # lookup. There is nothing this method could raise that is worth more than
        # calling the model.
        except Exception:
            self.stats.failure()
            self._go_down()
            # `logger.exception` rather than a warning: a cache that is down and a
            # bug in this file both arrive here, and the traceback is the only thing
            # that tells them apart. Same argument as the adapter's.
            logger.exception("The cache lookup failed; calling the model. %s", self.stats.line())
            return None

        stored = CachedAnswer.from_json(raw) if isinstance(raw, str) else None

        if stored is None:
            self.stats.miss()
            self._log("miss", started, None)
            return None

        self.stats.hit(stored.cost_usd)
        self._log("hit", started, stored)
        return stored

    def put(self, key: str, answer: CachedAnswer) -> None:
        # The half of the measurement that is easy to miss: a failed *read* is
        # followed by a write attempt that pays the same connect timeout again, so
        # a save cost two of them rather than one. This is what makes the number
        # 0 ms instead of 500.
        if self._is_down():
            return

        try:
            self._client.set(key, answer.to_json(), ex=self._ttl_seconds)
        except Exception:
            self.stats.failure()
            self._go_down()
            logger.exception("The cache write failed; the answer was still served.")

    def _is_down(self) -> bool:
        return self._clock() < self._silent_until

    def _go_down(self) -> None:
        self._silent_until = self._clock() + self._down_for_seconds

    def _log(self, outcome: str, started: float, stored: CachedAnswer | None) -> None:
        logger.info(
            "cache outcome=%s elapsed_ms=%.0f saved_now_usd=%s stored_model=%s %s",
            outcome,
            (time.perf_counter() - started) * 1000,
            # What this one hit did not spend, beside the running total. "unpriced"
            # rather than 0, so an entry stored before a price was configured is
            # visibly unknown instead of visibly free -- #64's rule about a zero
            # that quietly becomes a zero in whatever adds these up.
            "unpriced" if stored is None or stored.cost_usd is None else f"{stored.cost_usd:.6f}",
            "-" if stored is None else stored.model,
            self.stats.line(),
        )


def cache_from_env(env: Mapping[str, str]) -> AnswerCache | None:
    """A cache, or None -- which means every call is billed and says so once.

    **Nothing here stops the process**, which is the same call `_prices_from` makes
    and the opposite of `main.py`'s unrecognised `CATEGORIZER_PREDICTOR`. The test
    is what each mistake costs: a mistyped predictor serves the baseline while the
    deployment believes a model is running, and no number anywhere would say so,
    where a cache that failed to build costs money and is visible in the log the
    first time anybody looks at the bill. Taking a categorizer off the air to
    protect an optimisation is a worse trade than paying twice for a fortnight.

    The import is local, so `import categorizer.cache` needs no `redis` installed
    and the tests below run with it absent -- the same trick `from_env` uses for the
    `anthropic` SDK.
    """
    url = env.get(REDIS_URL_ENV, "").strip()

    if not url:
        # INFO and not a warning: no cache is a legal, cheap and entirely ordinary
        # configuration -- it is what every rules deployment has, and what a
        # developer running one model call by hand wants.
        logger.info(
            "%s is not set, so answers are not cached and every call is billed.", REDIS_URL_ENV
        )
        return None

    try:
        import redis
    except ImportError:
        logger.exception("%s is set but the redis package is missing; not caching.", REDIS_URL_ENV)
        return None

    try:
        client = redis.Redis.from_url(
            url,
            # Strings in and strings out. Without it `get` answers bytes, and
            # `isinstance(raw, str)` in `get` above would be False for every hit --
            # a cache that silently never hits, which is the failure shape this
            # whole issue is about.
            decode_responses=True,
            socket_connect_timeout=CONNECT_TIMEOUT_SECONDS,
            socket_timeout=SOCKET_TIMEOUT_SECONDS,
        )
    except Exception:
        # A malformed URL raises here rather than at the first call, which is the
        # one thing about this client that fails early.
        logger.exception("%s could not be used; not caching.", REDIS_URL_ENV)
        return None

    # **Deliberately no ping.** `from_url` connects lazily, and checking here would
    # buy a startup log line at the price of making a cold Redis look like a
    # misconfiguration -- and the first `get` reports it anyway, with the same
    # traceback and without having delayed the process that answers requests.
    logger.info("Caching answers in Redis, ttl=%ss.", _ttl_from(env))
    return RedisCache(client, ttl_seconds=_ttl_from(env))


def _ttl_from(env: Mapping[str, str]) -> int:
    raw = env.get(TTL_ENV, "").strip()

    if not raw:
        return DEFAULT_TTL_SECONDS

    try:
        ttl = int(raw)
    except ValueError:
        ttl = 0

    if ttl <= 0:
        logger.error(
            "%s=%r is not a positive number of seconds; using %s.", TTL_ENV, raw, DEFAULT_TTL_SECONDS
        )
        return DEFAULT_TTL_SECONDS

    return ttl


def _as_int(value: Any) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def _as_float(value: Any) -> float | None:
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else None
