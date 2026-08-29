"""Text into a vector, so that "nearest past transaction" is a question with an answer.

#66, and the first half of it. Nothing here decides a category; it turns a
description into 1024 floats, and `retrieval.py` is what does something with them.

**Anthropic has no embedding model.** Its own documentation says so and points at
Voyage AI, which is what this calls. That is a second vendor, a second key and a
second thing inside the save budget -- see the timeout below -- and it is the first
time this project has depended on anything but Anthropic for a model.

**The `voyageai` SDK lost, and it is the mirror image of why the `anthropic` SDK
won in #59.** There, `pyproject.toml` argues that hand-rolling means owning
"retries, streaming, error typing and the beta headers for a saving of one
package". None of that exists here: this is one POST, a bearer token, a flat JSON
body, no streaming, no tools, no betas. What the package costs was measured rather
than guessed -- `uv pip compile` resolves `voyageai` to **51 packages**, among them
`langchain-core`, `langsmith`, `huggingface-hub`, `numpy`, `pillow` and
`tokenizers`, and **three more HTTP stacks**: `httpx` 0.28, `requests` and
`aiohttp`, beside the `httpx2` that #59 consolidated this service onto. Fifty-one
packages and four HTTP libraries to send twenty lines of JSON is the trade
refused; the day this needs reranking, contextualised chunks or the multimodal
models, the SDK is the right answer and this file is the thing to delete.

`httpx2` is therefore *not* a new dependency: it is already in the runtime tree
under `anthropic` 1.2.0, and it is what `TestClient` uses. Nothing was added to
`pyproject.toml` for this file.
"""

import logging
from typing import Any, Final, Literal, Mapping, Protocol, Sequence

logger = logging.getLogger(__name__)

EMBED_URL: Final[str] = "https://api.voyageai.com/v1/embeddings"

API_KEY_ENV: Final[str] = "VOYAGE_API_KEY"
MODEL_ENV: Final[str] = "CATEGORIZER_EMBEDDING_MODEL"
DIMENSIONS_ENV: Final[str] = "CATEGORIZER_EMBEDDING_DIMENSIONS"
TIMEOUT_ENV: Final[str] = "CATEGORIZER_EMBEDDING_TIMEOUT_SECONDS"

# `voyage-4-lite` rather than `voyage-4` or `voyage-4-large`, and the reason is
# latency rather than money: all three are free under the 200 million tokens every
# account gets, and at roughly five tokens per description this project would need
# forty million transactions to leave that tier. What separates them here is that
# this call is spent inside the same eight seconds a save has (#59), so the model
# "optimized for latency and cost" is the one that fits. It is one environment
# variable away from the others, and `score.py` can measure whether the quality
# difference is real on descriptions this short -- which is exactly the question
# #66 says is not a foregone conclusion.
DEFAULT_MODEL: Final[str] = "voyage-4-lite"

# Voyage's own default. 256 and 512 are offered and are Matryoshka truncations --
# keep the leading dimensions, renormalise -- so they are a smaller pgvector column
# for some quality. Deliberately not taken for the first measurement: a truncation
# is a second variable, and a number that moved for two reasons at once says
# nothing about either. Worth revisiting when the column's width is the problem,
# which at a few thousand personal transactions it is not: 1024 floats is 4 KB, so
# a decade of weekly spending is about 2 MB.
DEFAULT_DIMENSIONS: Final[int] = 1024

# **Chosen against the budget, not against the network.** The .NET client allows
# one save eight seconds end to end and two to connect (#59); the Anthropic call
# inside it already takes about 2.1 seconds at effort=low (#60). This is the second
# network call on that path and it happens *before* the first, so its whole cost is
# added rather than overlapped. Two seconds is what is left over with room to
# spare, and the failure it buys is the right one: an embedding that does not
# arrive is a categorisation with no examples, which is the answer #60 already
# measured at 98.9%.
DEFAULT_TIMEOUT_SECONDS: Final[float] = 2.0

# Query or document, and this is the parameter that fails silently when it is
# wrong. Voyage prepends a different sentence to the text depending on which it is
# told -- "Represent the query for retrieving supporting documents: " against
# "Represent the document for retrieval: " -- so the two produce different vectors
# for identical text, on purpose. A corpus embedded as queries and searched with a
# query still returns *something*, ranked worse, with nothing anywhere reporting
# it: the retrieval simply gets quietly less useful, which is the failure shape
# this project keeps meeting and the reason this is a named type rather than a
# string argument that could be misspelt.
InputKind = Literal["query", "document"]


class Embedder(Protocol):
    """One text in, one vector out, plus what produced it.

    A Protocol for the same reason `Predictor` is one, and with the same payoff:
    the fake in `tests/` inherits nothing, so no test can reach the network by
    forgetting to substitute something.

    `model` and `dimensions` are on the port rather than on the implementation
    because they are what a *stored* vector has to be labelled with. #66's last
    trap is that changing the embedding model invalidates every vector already
    written, and a store that cannot ask its embedder what it is cannot enforce
    that -- it would compare a `voyage-4-lite` query against `voyage-4` documents
    and return confident nonsense.
    """

    @property
    def model(self) -> str: ...

    @property
    def dimensions(self) -> int: ...

    def embed(self, texts: Sequence[str], *, kind: InputKind) -> list[list[float]]: ...


class _Response(Protocol):
    def raise_for_status(self) -> Any: ...
    def json(self) -> Any: ...


class HttpClient(Protocol):
    """The sliver of `httpx2.Client` this file uses.

    Declared rather than imported for the reason `MessagesClient` is in
    `anthropic_predictor.py`: it is what lets a test hand in a stub with a canned
    body, so the suite never opens a socket and never spends a token.
    """

    def post(
        self, url: str, *, headers: Mapping[str, str], json: Any
    ) -> _Response: ...


class VoyageEmbedder:
    """The Voyage embeddings endpoint, and nothing else.

    **Raises on every failure**, which is the opposite of how `AnthropicPredictor`
    behaves and is deliberate. That class never raises because it sits on the path
    where a user's transaction is being saved and #39's promise is that a guess can
    never cost the row. This one is a layer lower: the thing that must not raise is
    *retrieval*, and `retrieval.py` is where the catch belongs -- so that "the
    embedder was unreachable" and "there were no neighbours" reach the prompt as
    the same absent example block, and reach the log as two different lines.
    """

    def __init__(
        self,
        client: HttpClient,
        api_key: str,
        model: str = DEFAULT_MODEL,
        dimensions: int = DEFAULT_DIMENSIONS,
    ) -> None:
        self._client = client
        self._api_key = api_key
        self._model = model
        self._dimensions = dimensions

    @property
    def model(self) -> str:
        return self._model

    @property
    def dimensions(self) -> int:
        return self._dimensions

    def embed(self, texts: Sequence[str], *, kind: InputKind) -> list[list[float]]:
        """One request for however many texts, in the order they were given.

        TODO(owner): the body. The spec, in order, and every step is there because
        of a way this fails quietly rather than for tidiness:

        1. An empty `texts` returns `[]` **without a request**. A POST with an
           empty input is a round trip that can only fail, and the caller that
           produces it is a corpus loader with nothing to load.
        2. POST `EMBED_URL` with `headers={"Authorization": f"Bearer {self._api_key}"}`
           and `json={"input": list(texts), "model": self._model,
           "input_type": kind, "output_dimension": self._dimensions}`.
           `input_type` is the parameter the `InputKind` comment above is about.
        3. `raise_for_status()`, then `.json()`.
        4. **Sort `data` by its `index` field before reading the embeddings.** The
           response is a list of `{"embedding": [...], "index": n}` and the order
           is not promised -- this is the Batches lesson (key by id, never by
           position) arriving at a second endpoint. Getting it wrong mislabels
           every vector in a batch against the wrong description, and the only
           symptom is retrieval that returns unrelated rows.
        5. Refuse a response whose length is not `len(texts)`, or any vector whose
           length is not `self._dimensions`, by raising **`ValueError`**. A short
           vector poisons the store permanently and pgvector would refuse it later
           with an error naming the column rather than the call.

           `ValueError` specifically, because `tests/test_embedding.py` asserts
           that type and it has to: the first draft of those tests said
           `pytest.raises(Exception)` and passed against this very stub, since
           `NotImplementedError` is an `Exception` -- and `RuntimeError` is no
           better, because `NotImplementedError` subclasses that too.
        6. Return the vectors.
        """
        raise NotImplementedError

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> "VoyageEmbedder | None":
        """The real client, or None when there is no key -- which is a legal state.

        None rather than a raise, and rather than the `logger.error`-and-carry-on
        that `AnthropicPredictor.from_env` does for a missing Anthropic credential.
        The difference is what each absence means. There, the model *is* the
        service, so a missing key makes every answer null and only a log line
        distinguishes that from a model declining everything. Here retrieval is an
        improvement to a predictor that already works: no key is "run without
        examples", which is a configuration somebody may want and is exactly what
        the off switch produces.

        Imported inside the function, the way `anthropic` is in `from_env` and
        `redis` is in `cache_from_env`, so that importing this module costs nothing
        and the rules path never loads an HTTP client it will not use.
        """
        api_key = env.get(API_KEY_ENV, "").strip()
        if not api_key:
            return None

        import httpx2

        return cls(
            # max_retries has no equivalent here -- httpx2 does not retry by
            # default -- which is the behaviour `anthropic` had to be told to have
            # with max_retries=0, for the arithmetic reason written there: a retry
            # that finishes after its caller has gone is billed and discarded.
            httpx2.Client(timeout=_timeout_from(env)),
            api_key,
            model=env.get(MODEL_ENV, "").strip() or DEFAULT_MODEL,
            dimensions=_dimensions_from(env),
        )


def _timeout_from(env: Mapping[str, str]) -> float:
    raw = env.get(TIMEOUT_ENV, "").strip()
    if not raw:
        return DEFAULT_TIMEOUT_SECONDS

    try:
        return float(raw)
    except ValueError:
        # Carries on at the default rather than refusing to start, for the same
        # reason `_prices_from` does in `anthropic_predictor.py`: the worst case is
        # a call that waits the wrong length of time, and taking the categorizer
        # off the air over a mistyped number protects nothing. The value is a
        # duration, not a credential, so naming it is what makes it fixable.
        logger.error(
            "%s=%r is not a number, so the default of %ss is used.",
            TIMEOUT_ENV,
            raw,
            DEFAULT_TIMEOUT_SECONDS,
        )
        return DEFAULT_TIMEOUT_SECONDS


def _dimensions_from(env: Mapping[str, str]) -> int:
    raw = env.get(DIMENSIONS_ENV, "").strip()
    if not raw:
        return DEFAULT_DIMENSIONS

    try:
        return int(raw)
    except ValueError:
        logger.error(
            "%s=%r is not a whole number, so the default of %s is used.",
            DIMENSIONS_ENV,
            raw,
            DEFAULT_DIMENSIONS,
        )
        return DEFAULT_DIMENSIONS
