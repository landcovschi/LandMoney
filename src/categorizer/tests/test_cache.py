"""The answer cache -- #65, driven entirely by a stub client.

**Nothing here opens a socket**, which is the same property `test_anthropic_predictor.py`
holds for the model and is held the same way: `RedisCache` takes its client as a
constructor argument, so a test that forgot to pass one fails to construct rather
than quietly reaching a Redis that may or may not be running on the machine that
happens to be executing the suite. CI has no Redis service, deliberately -- see
`ci.yml`.

`StubRedis` inherits nothing, which is `_RedisClient` being a structural Protocol,
the same trick as `StubClient` and `FakePredictor`.

The file is in two halves, and the first is the one that matters. The key is what
#65 calls the sharpest thing in the issue: a normalisation that lives in the cache
path and nowhere else silently makes the recorded score a number about code that no
longer runs. So the tests below assert that **nothing is normalised** -- which is an
odd thing to assert until you notice that every one of them would pass if somebody
added a `.strip().lower()` "improvement" and only these say no.
"""

import json
import logging

import pytest

from categorizer.cache import (
    CONNECT_TIMEOUT_SECONDS,
    DEFAULT_TTL_SECONDS,
    KEY_PREFIX,
    REDIS_URL_ENV,
    SOCKET_TIMEOUT_SECONDS,
    TTL_ENV,
    CachedAnswer,
    CacheStats,
    RedisCache,
    cache_from_env,
    key_for,
)
from categorizer.categories import NO_PREDICTION

# --- the fake ----------------------------------------------------------------


class StubRedis:
    """Shaped like `redis.Redis` in the two places `RedisCache` touches it."""

    def __init__(self, raises: BaseException | None = None) -> None:
        self.stored: dict[str, str] = {}
        self.writes: list[tuple[str, str, int | None]] = []
        # Counted rather than inferred from `writes`, because the tests that matter
        # below are about calls that must *not* happen -- a socket the cache is
        # supposed to be leaving alone.
        self.asked = 0
        self.raises = raises

    def get(self, name: str) -> object:
        self.asked += 1
        if self.raises is not None:
            raise self.raises
        return self.stored.get(name)

    def set(self, name: str, value: str, ex: int | None = None) -> object:
        self.asked += 1
        if self.raises is not None:
            raise self.raises
        self.writes.append((name, value, ex))
        self.stored[name] = value
        return True


def an_answer(answer: str = "groceries", cost_usd: float | None = 0.0012) -> CachedAnswer:
    return CachedAnswer(answer, "claude-opus-5", 1200, 8, cost_usd)


def a_key(**overrides: str) -> str:
    parts = {
        "model": "claude-opus-5",
        "effort": "low",
        "prompt_fingerprint": "c8ad9d9fd16f",
        "user_message": "Description: linella\nAmount: 312.40\nCurrency: MDL",
    }
    return key_for(**{**parts, **overrides})


# --- the key ------------------------------------------------------------------


def test_the_same_input_is_the_same_key():
    """The whole issue in one line: identical input must not be billed twice."""
    assert a_key() == a_key()


def test_the_key_is_namespaced_so_a_shared_redis_stays_legible():
    assert a_key().startswith(KEY_PREFIX)


@pytest.mark.parametrize(
    "message",
    [
        "Description: LINELLA\nAmount: 312.40\nCurrency: MDL",
        "Description:  linella\nAmount: 312.40\nCurrency: MDL",
        "Description: linella \nAmount: 312.40\nCurrency: MDL",
        "Description: Linella\nAmount: 312.40\nCurrency: MDL",
    ],
)
def test_nothing_is_normalised_when_the_key_is_built(message: str):
    """**#65's sharpest trap, and the reason this file exists.**

    Every one of these differs from the baseline only in case or whitespace, and
    every one of them is a different key. That looks like a missed optimisation and
    it is the point: the description reaches `rules.py` and the model exactly as it
    was typed, so a cache that folded `LINELLA` onto `linella` would be a rule that
    exists in one path and nowhere else -- the same class of drift as the mutation
    caught by hand in #39, where a well-meaning `.replace("-", " ")` in the service
    silently made the recorded baseline a number about code that no longer ran.

    The day folding is genuinely wanted, it belongs in `_user_message` -- where the
    model sees it too, and where the eval number moves in the same commit and says
    so.
    """
    assert a_key(user_message=message) != a_key()


def test_the_model_is_in_the_key():
    """Otherwise a switch to a cheaper model serves the expensive one's answers and
    reports them under the new model's name."""
    assert a_key(model="claude-sonnet-5") != a_key()


def test_the_effort_is_in_the_key():
    """`effort` is the one lever #59 left for #60 to measure. Raising it and getting
    the old answers back would make the measurement report that it changed nothing."""
    assert a_key(effort="high") != a_key()


def test_the_prompt_version_is_in_the_key():
    """#65's second trap in as many words: without this, the first prompt edit
    serves yesterday's answers forever and the eval run after that edit measures a
    cache rather than a model."""
    assert a_key(prompt_fingerprint="000000000000") != a_key()


def test_the_parts_cannot_be_confused_with_each_other():
    """The framing question the join raises, answered rather than assumed.

    Joining fields with a separator that can appear inside a field is how two
    different tuples come to share a key. Here only `user_message` can contain a
    newline and it is last, so shifting a boundary produces a different string.
    """
    assert a_key(model="claude-opus-5\nlow") != a_key()


# --- what is stored, and what is refused on the way back out -------------------


def test_an_entry_survives_the_round_trip():
    stored = an_answer()

    assert CachedAnswer.from_json(stored.to_json()) == stored


def test_an_abstention_is_storable():
    """It is an answer: asking again buys the same `unknown` at the same price."""
    assert CachedAnswer.from_json(an_answer(NO_PREDICTION).to_json()).answer == NO_PREDICTION


def test_an_entry_that_never_reported_its_usage_survives():
    """A call whose response carried no usage still produced an answer worth
    keeping; only the money is unknown, and `None` is how that is said."""
    stored = CachedAnswer("groceries", "claude-opus-5", None, None, None)

    assert CachedAnswer.from_json(stored.to_json()) == stored


@pytest.mark.parametrize(
    "raw",
    [
        "not json at all",
        "[]",
        '{"model": "claude-opus-5"}',
        '{"answer": null}',
        '{"answer": 7}',
        '{"answer": ["groceries"]}',
    ],
)
def test_an_unreadable_entry_reads_as_a_miss(raw: str):
    assert CachedAnswer.from_json(raw) is None


def test_a_cached_answer_outside_the_vocabulary_is_refused():
    """**The one check in this file that is about correctness rather than cost.**

    Redis is the only store in this system something other than this code can write
    to, and everything else here -- the closed vocabulary, the response schema,
    `_answer_from` -- exists to stop a twelfth category reaching
    `transactions.category`. A cache that served one back would be a way around all
    of it.
    """
    assert CachedAnswer.from_json(json.dumps({"answer": "takeaway"})) is None


# --- the cache itself ---------------------------------------------------------


def test_a_stored_answer_comes_back():
    client = StubRedis()
    cache = RedisCache(client)
    cache.put("k", an_answer())

    assert cache.get("k") == an_answer()


def test_an_entry_is_written_with_a_ttl():
    """Not staleness -- the key already carries the model and the prompt. What the
    TTL bounds is a Redis holding entries for descriptions nobody will type again."""
    client = StubRedis()

    RedisCache(client, ttl_seconds=99).put("k", an_answer())

    assert client.writes == [("k", an_answer().to_json(), 99)]


def test_a_cold_key_is_a_miss_and_not_an_error():
    cache = RedisCache(StubRedis())

    assert cache.get("k") is None
    assert (cache.stats.misses, cache.stats.failures) == (1, 0)


def test_bytes_from_a_client_that_does_not_decode_are_a_miss_rather_than_a_crash():
    """The `decode_responses=True` trap, guarded from the other side.

    Without that flag `redis.Redis.get` answers bytes, and a cache that never hits
    is exactly the failure this whole issue is about: everything stays green, every
    answer is correct, and the bill does not move. Here it degrades to a miss
    instead of raising on the save path -- but the test exists so the flag cannot be
    dropped without something saying so.
    """
    client = StubRedis()
    client.stored["k"] = an_answer().to_json().encode("utf-8")

    assert RedisCache(client).get("k") is None


def test_redis_being_down_means_call_the_model_and_never_no_category(caplog):
    """#65's third trap, and the promise this module shares with the two layers
    above it: a dependency the system is designed to run without may not become a
    second way to fail."""
    caplog.set_level(logging.INFO)
    cache = RedisCache(StubRedis(raises=OSError("connection refused")))

    assert cache.get("k") is None
    assert cache.stats.failures == 1
    # Not a miss. A cold key and a dead Redis both end in a model call, and only
    # one of them is worth an alarm -- counting them together would hide it.
    assert cache.stats.misses == 0
    assert "Traceback" in caplog.text


class Clock:
    """Six lines, which is why `Microsoft.Extensions.TimeProvider.Testing`'s Python
    equivalent is not a dependency -- the same call CLAUDE.md records for the .NET
    side. Sleeping instead would put a real thirty seconds in the suite, or a
    shortened window that tests a number nothing runs."""

    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


def test_a_failed_write_does_not_reach_the_caller():
    """The answer has already been produced and paid for; losing it to a cache
    write would be the cache costing a transaction the model call did not."""
    cache = RedisCache(StubRedis(raises=OSError("connection refused")))

    cache.put("k", an_answer())

    assert cache.stats.failures == 1


# --- the hit rate -- #65's third bullet ---------------------------------------


def test_the_hit_rate_is_reported_on_every_lookup(caplog):
    """A cache nobody measured is a cache nobody knows is working.

    The totals ride on every line rather than being reported at the end, because
    this container scales to zero (#61) and there is no end -- the last line a
    replica writes is its whole story.
    """
    caplog.set_level(logging.INFO)
    client = StubRedis()
    cache = RedisCache(client)
    cache.put("k", an_answer())

    cache.get("k")
    cache.get("cold")

    lines = [r.getMessage() for r in caplog.records if r.getMessage().startswith("cache ")]
    assert "outcome=hit" in lines[0]
    assert "hits=1 misses=0 failures=0 hit_rate=100.0%" in lines[0]
    assert "outcome=miss" in lines[1]
    assert "hits=1 misses=1 failures=0 hit_rate=50.0%" in lines[1]


def test_the_money_a_hit_did_not_spend_is_the_money_that_call_cost():
    """What the call cost when it was made, not what those tokens cost today.

    #64 keeps prices out of the code because a stale figure in a log is believed;
    the same argument says a saving must not be recomputed later at a price the
    call was never charged.
    """
    stats = CacheStats()

    stats.hit(0.0012)
    stats.hit(0.0012)

    assert stats.saved_usd == pytest.approx(0.0024)


def test_a_hit_on_an_unpriced_entry_still_counts_as_a_hit():
    """The call was saved either way. Adding a zero would report a saving of
    nothing, which is the one number that is certainly wrong."""
    stats = CacheStats()

    stats.hit(None)

    assert (stats.hits, stats.saved_usd) == (1, 0.0)


def test_the_hit_rate_of_nothing_at_all_is_not_a_division_by_zero():
    assert "hit_rate=0.0%" in CacheStats().line()


def test_a_failure_counts_against_the_hit_rate():
    """A lookup that got no answer because the cache was unavailable is a lookup.
    Leaving failures out of the denominator would report a healthy 100% for a cache
    that answered once and was down for the rest of the day."""
    stats = CacheStats()

    stats.hit(None)
    stats.failure()

    assert "hit_rate=50.0%" in stats.line()


# --- configuration ------------------------------------------------------------


def test_no_url_is_no_cache_and_is_not_an_error(caplog):
    """The state every rules deployment is in, and the state a developer running
    one call by hand wants. INFO, not a warning."""
    caplog.set_level(logging.INFO)

    assert cache_from_env({}) is None
    assert REDIS_URL_ENV in caplog.text
    assert not [r for r in caplog.records if r.levelno >= logging.WARNING]


def test_a_blank_url_is_the_same_as_no_url():
    """`${CATEGORIZER_REDIS_URL:-}` in a compose file and an empty Container Apps
    variable both arrive as an empty string -- the same reading `main.py` gives
    CATEGORIZER_PREDICTOR."""
    assert cache_from_env({REDIS_URL_ENV: "   "}) is None


def test_a_url_that_cannot_be_used_is_no_cache_rather_than_no_service(caplog):
    """The same call `_prices_from` makes: taking a categorizer off the air to
    protect an optimisation is a worse trade than paying twice for a fortnight."""
    assert cache_from_env({REDIS_URL_ENV: "postgres://nonsense"}) is None
    assert "Traceback" in caplog.text


def test_the_client_is_built_with_the_settings_that_make_it_safe(monkeypatch):
    """Asserted rather than assumed, for the reason #59 gives about the request
    shape: every one of these being wrong fails as something other than itself.

    Without `decode_responses` the client answers bytes, `from_json` never sees a
    string, and **every hit becomes a miss** -- a cache that is perfectly correct,
    perfectly green, and does not save a penny. Without the two timeouts a Redis
    that accepts a connection and stops answering holds the save path open past the
    budget the .NET side capped at eight seconds.
    """
    import redis

    captured: dict[str, object] = {}

    def fake_from_url(url: str, **kwargs: object) -> object:
        captured.update(kwargs, url=url)
        return object()

    monkeypatch.setattr(redis.Redis, "from_url", fake_from_url)
    cache_from_env({REDIS_URL_ENV: "redis://localhost:6379/0"})

    assert captured["decode_responses"] is True
    assert captured["socket_connect_timeout"] == CONNECT_TIMEOUT_SECONDS
    assert captured["socket_timeout"] == SOCKET_TIMEOUT_SECONDS


def test_a_ttl_can_be_configured():
    cache = cache_from_env({REDIS_URL_ENV: "redis://localhost:6379/0", TTL_ENV: "60"})

    assert cache._ttl_seconds == 60


@pytest.mark.parametrize("raw", ["nonsense", "0", "-1"])
def test_an_unusable_ttl_is_the_default_and_says_so(raw: str, caplog):
    """Not a raise. The worst case is entries living longer than intended, which is
    not worth refusing to start over -- and it is the same reasoning `_prices_from`
    applies to an unparseable price."""
    cache = cache_from_env({REDIS_URL_ENV: "redis://localhost:6379/0", TTL_ENV: raw})

    assert cache._ttl_seconds == DEFAULT_TTL_SECONDS
    assert TTL_ENV in caplog.text


# --- what a cache that is not there costs -------------------------------------
#
# Measured before this existed, with the container stopped: a lookup and then a
# write each paid the connect timeout in full, because a stopped container leaves
# the SYN unanswered rather than refusing it -- #39's finding about the categorizer,
# one service along. **1055 ms added to every save**, on the path where a user's
# transaction is being written, until somebody noticed. These four tests are what
# turns that into 0 ms.


def test_after_a_failure_the_socket_is_left_alone():
    client = StubRedis(raises=OSError("connection refused"))
    cache = RedisCache(client, clock=Clock())

    cache.get("k")
    cache.get("k")
    cache.get("k")

    assert client.asked == 1


def test_a_write_after_a_failed_read_is_not_attempted_either():
    """The half that is easy to miss, and half the measured cost: a failed read was
    followed by a write attempt paying the same timeout again."""
    client = StubRedis(raises=OSError("connection refused"))
    cache = RedisCache(client, clock=Clock())

    cache.get("k")
    cache.put("k", an_answer())

    assert client.asked == 1


def test_a_suppressed_lookup_is_still_counted_and_still_logged(caplog):
    """So a hit rate of zero is never a mystery. One line per lookup however it
    ends -- but no traceback, since the first failure already carries it and
    repeating it every save for thirty seconds would bury it."""
    caplog.set_level(logging.INFO)
    cache = RedisCache(StubRedis(raises=OSError("nope")), clock=Clock())

    cache.get("k")
    caplog.clear()
    cache.get("k")

    assert "outcome=down" in caplog.text
    assert "Traceback" not in caplog.text
    assert (cache.stats.failures, cache.stats.hits, cache.stats.misses) == (2, 0, 0)


def test_the_cache_is_used_again_once_the_window_passes():
    """The price of the whole arrangement, asserted rather than assumed: a Redis
    that comes back is not used for up to thirty seconds. Misses, never wrong
    answers."""
    clock = Clock()
    client = StubRedis(raises=OSError("connection refused"))
    cache = RedisCache(client, down_for_seconds=30.0, clock=clock)
    cache.get("k")

    client.raises = None
    clock.now += 29.0
    cache.get("k")
    assert client.asked == 1

    clock.now += 2.0
    cache.get("k")
    assert client.asked == 2
