"""The Voyage embedder -- #66, driven entirely by a stub HTTP client.

**Nothing here opens a socket.** Same property as `test_cache.py` and
`test_anthropic_predictor.py`, held the same way: `VoyageEmbedder` takes its client
as a constructor argument, so a test that forgot to pass one fails to construct
rather than quietly reaching `api.voyageai.com` and spending somebody's token
allowance. `StubHttp` inherits nothing, which is `HttpClient` being a structural
Protocol.

These were written **before the body of `embed`**, against the numbered spec in its
docstring, and that ordering is the point rather than a scheduling accident. Two of
the six steps fail in ways that are invisible from the outside -- a response read
positionally pairs every vector with the wrong description, and a short vector
poisons a store permanently -- so a suite written afterwards is a suite written by
somebody who has already decided what the code does.

What is deliberately *not* asserted anywhere here: that the vectors are any good.
That is `evals/score.py`'s job and it costs money; this file is about the request
being the one that was meant and the response being read the way it arrives.
"""

import json

import pytest

from categorizer.embedding import (
    DEFAULT_DIMENSIONS,
    DEFAULT_MODEL,
    DEFAULT_TIMEOUT_SECONDS,
    DIMENSIONS_ENV,
    EMBED_URL,
    MODEL_ENV,
    TIMEOUT_ENV,
    VoyageEmbedder,
    _dimensions_from,
    _timeout_from,
)

# --- the fake ----------------------------------------------------------------


class Unauthorized(Exception):
    """Stands in for whatever `httpx2` raises out of `raise_for_status`.

    Defined here rather than reaching for a builtin because the builtins that
    read naturally -- `Exception`, `RuntimeError` -- are both satisfied by
    `NotImplementedError`, and therefore by a body nobody has written yet.
    """


class StubResponse:
    def __init__(self, payload: object, error: BaseException | None = None) -> None:
        self._payload = payload
        self._error = error

    def raise_for_status(self) -> object:
        if self._error is not None:
            raise self._error
        return self

    def json(self) -> object:
        return self._payload


class StubHttp:
    """Shaped like `httpx2.Client` in the one place `VoyageEmbedder` touches it."""

    def __init__(
        self, payload: object = None, error: BaseException | None = None
    ) -> None:
        self._payload = payload
        self._error = error
        # A list rather than a single slot, because the test that matters most
        # below is about a call that must *not* happen.
        self.calls: list[dict[str, object]] = []

    def post(self, url: str, *, headers: object, json: object) -> StubResponse:
        self.calls.append({"url": url, "headers": headers, "json": json})
        return StubResponse(self._payload, self._error)


def body_of(
    count: int, dimensions: int = DEFAULT_DIMENSIONS, *, shuffled: bool = False
) -> dict:
    """A response shaped like Voyage's, with each vector carrying its own index.

    Every vector is distinguishable -- row `i` is `[i, i, ...]` -- so a test can
    say which description a vector was paired with rather than merely that some
    floats came back.

    **The default width is the embedder's, and it started at a convenient 4.**
    That was a bug in this helper rather than in the code, and the body found it
    the first time it ran: a four-float vector against a request for 1024 is
    exactly what step 5 refuses, so six tests failed with the width error. Worth
    leaving written down, because it is the mirror of the defect this file found
    in itself an hour earlier -- a suite written before the body is not thereby
    correct, it is merely written first.
    """
    data = [
        {"embedding": [float(i)] * dimensions, "index": i} for i in range(count)
    ]
    if shuffled:
        data.reverse()
    return {"object": "list", "data": data, "model": DEFAULT_MODEL}


def embedder(http: StubHttp, **kwargs: object) -> VoyageEmbedder:
    return VoyageEmbedder(http, "key-that-is-never-real", **kwargs)  # type: ignore[arg-type]


# --- the request -------------------------------------------------------------


def test_an_empty_list_is_answered_without_a_request() -> None:
    """Step 1. A POST with an empty input is a round trip that can only fail.

    The caller that produces this is a corpus loader with nothing to load, and on
    the save path it is the difference between "no examples" costing nothing and
    costing the full timeout.
    """
    http = StubHttp(body_of(0))

    assert embedder(http).embed([], kind="document") == []
    assert http.calls == []


def test_the_request_is_the_one_voyage_documents() -> None:
    http = StubHttp(body_of(1))

    embedder(http).embed(["linella"], kind="query")

    sent = http.calls[0]
    assert sent["url"] == EMBED_URL
    assert sent["headers"]["Authorization"] == "Bearer key-that-is-never-real"
    assert sent["json"] == {
        "input": ["linella"],
        "model": DEFAULT_MODEL,
        "input_type": "query",
        "output_dimension": DEFAULT_DIMENSIONS,
    }


def test_the_key_is_never_in_the_body_or_the_url() -> None:
    """It travels in one header and nowhere else.

    CLAUDE.md's rule about `.env` is that a key never reaches a log, an example
    command or a file. A URL is the likeliest accident of the three -- it is what
    ends up in an error message, a traceback and a proxy's access log.
    """
    http = StubHttp(body_of(1))

    embedder(http).embed(["linella"], kind="query")

    sent = http.calls[0]
    assert "key-that-is-never-real" not in sent["url"]
    assert "key-that-is-never-real" not in json.dumps(sent["json"])


def test_the_kind_reaches_the_wire_as_input_type() -> None:
    """The parameter that fails silently -- see `InputKind` in embedding.py.

    Voyage prepends a different sentence for a query than for a document, so a
    corpus embedded as queries still returns neighbours, ranked worse, with
    nothing anywhere reporting it. This is the only place that word is checked.
    """
    http = StubHttp(body_of(1))

    embedder(http).embed(["rent july"], kind="document")

    assert http.calls[0]["json"]["input_type"] == "document"


def test_the_configured_model_and_dimensions_are_what_is_asked_for() -> None:
    http = StubHttp(body_of(1, dimensions=256))

    embedder(http, model="voyage-4", dimensions=256).embed(["x"], kind="query")

    assert http.calls[0]["json"]["model"] == "voyage-4"
    assert http.calls[0]["json"]["output_dimension"] == 256


def test_many_texts_are_one_request() -> None:
    """A corpus is embedded in a batch, not a row at a time.

    Not an optimisation: the free tier is counted in tokens rather than requests,
    so this is about the loader for a few thousand rows taking seconds instead of
    minutes, and about not being rate-limited into a retry loop this file does not
    own.
    """
    http = StubHttp(body_of(3))

    embedder(http).embed(["a", "b", "c"], kind="document")

    assert len(http.calls) == 1
    assert http.calls[0]["json"]["input"] == ["a", "b", "c"]


# --- the response ------------------------------------------------------------


def test_the_vectors_come_back_in_the_order_the_texts_went_in() -> None:
    http = StubHttp(body_of(3))

    vectors = embedder(http).embed(["a", "b", "c"], kind="document")

    # The first float identifies the row; the width is asserted separately so a
    # failure says which of the two went wrong.
    assert [vector[0] for vector in vectors] == [0.0, 1.0, 2.0]
    assert all(len(vector) == DEFAULT_DIMENSIONS for vector in vectors)


def test_a_response_out_of_order_is_sorted_by_its_index() -> None:
    """Step 4, and the one mistake here that produces no error at all.

    The response is a list of `{"embedding": ..., "index": n}` and the order is not
    promised -- the same "key by id, never by position" rule the Batches API has.
    Read positionally, every vector in a batch is stored against the wrong
    description, and the only symptom is a retrieval step that returns unrelated
    rows: no exception, no log line, and a score that is merely worse.

    This is the test that fails for a body that looks completely reasonable.
    """
    http = StubHttp(body_of(3, shuffled=True))

    vectors = embedder(http).embed(["a", "b", "c"], kind="document")

    assert [vector[0] for vector in vectors] == [0.0, 1.0, 2.0]


def test_too_few_vectors_is_refused() -> None:
    """Step 5. Carrying on would pair vectors with the wrong descriptions again.

    `ValueError` and not a bare `Exception`, and that is not style. The first
    version of this file said `pytest.raises(Exception)` and **passed against the
    unimplemented stub**, because `NotImplementedError` is an `Exception` -- so
    the three tests here that assert a refusal were green before a line of the
    body existed. The same trap caught `pytest.raises(RuntimeError)` one step
    later: `NotImplementedError` subclasses `RuntimeError` too.

    This is #64's lesson in a new coat -- a test that asserts only that something
    went wrong cannot tell a rule from the absence of the code that holds it.
    """
    http = StubHttp(body_of(2))

    with pytest.raises(ValueError):
        embedder(http).embed(["a", "b", "c"], kind="document")


def test_a_vector_of_the_wrong_width_is_refused() -> None:
    """Step 5, the half that matters later rather than now.

    A short vector is accepted by an in-memory store and stored happily; pgvector
    refuses it at insert with an error naming the *column*, which is four steps
    from the call that produced it. Refusing here is what keeps the message near
    the cause.
    """
    http = StubHttp(body_of(1, dimensions=DEFAULT_DIMENSIONS - 1))

    with pytest.raises(ValueError):
        embedder(http).embed(["a"], kind="document")


def test_an_http_error_is_raised_rather_than_swallowed() -> None:
    """`VoyageEmbedder` raises where `AnthropicPredictor` never does.

    The layers are the reason, and it is written out in the class docstring: the
    thing that must not raise is *retrieval*, so the catch belongs one file up in
    `retrieval.py` -- where "the embedder was unreachable" and "there were no
    neighbours" become the same absent example block in the prompt and two
    different lines in the log. Swallowing it here would collapse that distinction
    at the layer least able to report it.

    The exception is one this file defines rather than a builtin, for the reason
    written on `test_too_few_vectors_is_refused`: every builtin broad enough to
    stand in for "whatever httpx2 raises" is also broad enough to be satisfied by
    an unwritten body.
    """
    http = StubHttp(None, error=Unauthorized("401"))

    with pytest.raises(Unauthorized):
        embedder(http).embed(["a"], kind="query")


# --- configuration -----------------------------------------------------------


def test_no_key_is_no_embedder_and_not_an_error() -> None:
    """Running without examples is a configuration, not a failure.

    Deliberately unlike `AnthropicPredictor.from_env`, which logs an error for a
    missing Anthropic credential because there the model *is* the service. Here
    retrieval is an improvement to a predictor that already works, so an absent key
    produces exactly what the off switch produces.
    """
    assert VoyageEmbedder.from_env({}) is None
    assert VoyageEmbedder.from_env({"VOYAGE_API_KEY": "   "}) is None


@pytest.mark.parametrize(
    "value, expected",
    [("", DEFAULT_TIMEOUT_SECONDS), ("0.5", 0.5), ("not a number", DEFAULT_TIMEOUT_SECONDS)],
)
def test_an_unreadable_timeout_falls_back_rather_than_stopping_the_service(
    value: str, expected: float
) -> None:
    """Same call as `_prices_from`'s, for the same reason.

    The worst case is a call that waits the wrong length of time. Taking a
    categorizer off the air over a mistyped duration protects nothing -- which is
    deliberately the opposite of `main.py`'s unrecognised CATEGORIZER_PREDICTOR,
    where the wrong value serves the rules while the deployment believes a model is
    running.
    """
    assert _timeout_from({TIMEOUT_ENV: value}) == expected


@pytest.mark.parametrize(
    "value, expected",
    [("", DEFAULT_DIMENSIONS), ("256", 256), ("wide", DEFAULT_DIMENSIONS)],
)
def test_an_unreadable_dimension_falls_back(value: str, expected: int) -> None:
    assert _dimensions_from({DIMENSIONS_ENV: value}) == expected


def test_the_model_can_be_named_by_the_environment() -> None:
    """#66's last trap needs this to be readable, not just settable.

    Changing the embedding model invalidates every stored vector, so the store has
    to record which model produced them -- and it can only do that by asking the
    embedder. `model` being a property on the port rather than a constant in this
    file is what makes that possible.
    """
    embedder_from_env = VoyageEmbedder.from_env(
        {"VOYAGE_API_KEY": "k", MODEL_ENV: "voyage-4-large"}
    )

    assert embedder_from_env is not None
    assert embedder_from_env.model == "voyage-4-large"
    assert embedder_from_env.dimensions == DEFAULT_DIMENSIONS
