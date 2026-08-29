"""Which of this person's own past transactions look like this one -- #66.

`embedding.py` turns a description into floats; this decides what to do with them,
and it is deliberately the only file that knows there is more than one way to
measure "similar".

**Two implementations, and the second is not a fallback.** `VectorStore` is what
#66 asks for. `LexicalStore` compares character trigrams and needs no vendor, no
key and no network -- it is the **control**, and it exists because the issue says
out loud that whether embeddings beat substring matching on two- and three-word
merchant names is a real question. A vector store scored against nothing is a
vector store that cannot be shown to have earned its dependency. `evals/score.py`
runs all three of off, lexical and vector.

**Where the examples come from matters more than how they are found.** #66's first
trap: they must be labels a person confirmed, never the model's own past guesses,
or the model is taught its own mistakes and the score cannot see it happening
because it was never measured on those rows. Nothing in this file enforces that --
it takes the examples it is given. `category_source = human` (#63) is the filter,
and it is applied by whoever builds the corpus: `PgVectorExampleStore`'s query for
the service, and the eval set's own labels for the scorer.

**Nothing here raises.** `neighbours_for` is the one door, and it swallows
everything: an embedder that cannot reach Voyage, a database that is down, a bug
in the cosine. The result is an empty list, which is a prompt with no examples,
which is the predictor #60 already measured at 98.9%. That is the same promise
`cache.py` makes about Redis and `CategorizerClient` makes about this whole
service -- and, per #64, it is the third time it means an absence has to be
counted or nobody would ever know.
"""

import logging
import math
import time
from typing import Final, Mapping, NamedTuple, Protocol, Sequence

from categorizer.embedding import Embedder

logger = logging.getLogger(__name__)

# Five, and the number is a guess bounded on both sides rather than a tuned value.
# Too few and a single unrelated neighbour is a third of the evidence; too many and
# the prompt fills with rows that merely share a letter, which is the failure mode
# #66's fourth trap describes. It is deliberately not tuned against the eval set:
# picking k by trying values until the number goes up is exactly what `holdout.csv`
# exists to catch, and would make the improvement a property of these 53 rows.
DEFAULT_EXAMPLE_COUNT: Final[int] = 5

RETRIEVAL_ENV: Final[str] = "CATEGORIZER_RETRIEVAL"
EXAMPLE_COUNT_ENV: Final[str] = "CATEGORIZER_EXAMPLE_COUNT"

# The three settings #66 asks for -- "turning retrieval off is one setting, so the
# comparison stays repeatable after the fact".
MODES: Final[tuple[str, ...]] = ("off", "lexical", "vector")


class Example(NamedTuple):
    """One past transaction a person has confirmed the category of.

    Deliberately just the two fields. The amount and the currency are in the
    request because a 4.50 and a 450 at one merchant are different purchases
    (`prompt.py`), and they are *not* here: an example's job is to say what this
    person means by a word, and a past amount is evidence about a past purchase
    rather than about this one. Adding them is one field and a prompt change, and
    it would have to be measured rather than assumed.
    """

    description: str
    category: str


class Neighbour(NamedTuple):
    """An example and how near it was, on a scale where higher is nearer.

    The score crosses no boundary and reaches no prompt -- it is for the log line
    and for `--show-examples`, which is #66's second acceptance test: "the examples
    chosen for a given description can be inspected". A retrieval step nobody can
    look at is untestable.

    **The two stores' scores are not comparable to each other**, and no code here
    pretends otherwise. Cosine over Voyage vectors sits somewhere around 0.4-0.9
    even for unrelated short strings; trigram Jaccard is near 0 for them. That is
    why there is no minimum-score floor below which a neighbour is dropped: one
    threshold cannot mean the same thing to both, and a per-store threshold tuned
    until the eval improves is the eval teaching the answers.
    """

    example: Example
    score: float


class ExampleStore(Protocol):
    """Nearest first, at most k of them.

    A Protocol for the third time in this package, and for the reason `Predictor`
    is one: `evals/score.py` builds an in-memory store, the service builds a
    pgvector one, and neither imports the other. `label` exists so a score can say
    what produced it -- #60's rule that a number without its configuration beside
    it is not reproducible, arriving at a second knob.
    """

    @property
    def label(self) -> str: ...

    def nearest(self, description: str, *, k: int) -> list[Neighbour]: ...


class VectorStore:
    """Cosine over embeddings held in memory. What the scorer runs.

    In memory rather than in Postgres on purpose: the eval corpus is 53 rows, and a
    database would make `python evals/score.py` need one. `PgVectorExampleStore` is
    the same query against a table for a corpus that is somebody's real history,
    and the two are checked against each other rather than assumed to agree.

    Pure Python cosine, no numpy. 53 rows x 1024 floats is about 50k
    multiplications per lookup, which is under a millisecond and is nothing beside
    the 2.1-second model call it precedes. It is O(n) per query and would stop
    being a good idea in the low thousands -- which is the point at which pgvector's
    index is the answer rather than a faster loop here.
    """

    def __init__(
        self,
        examples: Sequence[Example],
        vectors: Sequence[Sequence[float]],
        embedder: Embedder,
    ) -> None:
        if len(examples) != len(vectors):
            # The failure this guards is silent and permanent: a mismatch means
            # every example is paired with the wrong vector, and the only symptom
            # is retrieval that returns unrelated rows. Same failure `embed`'s
            # index sort exists to prevent, one layer up.
            raise ValueError(
                f"{len(examples)} examples and {len(vectors)} vectors do not pair up."
            )

        self._examples = list(examples)
        self._vectors = [list(vector) for vector in vectors]
        self._embedder = embedder

    @property
    def label(self) -> str:
        return f"vector({self._embedder.model}, {self._embedder.dimensions}d, {len(self._examples)} rows)"

    @classmethod
    def build(cls, examples: Sequence[Example], embedder: Embedder) -> "VectorStore":
        """Embed the whole corpus in one request, as documents.

        `kind="document"` and not `"query"`, which is the parameter `embedding.py`
        says fails silently: Voyage prepends a different sentence to each, so a
        corpus embedded as queries still returns neighbours, ranked worse, with
        nothing reporting it. This is one of the two call sites where that word is
        chosen, and `nearest` below is the other.
        """
        vectors = embedder.embed([e.description for e in examples], kind="document")
        return cls(examples, vectors, embedder)

    def nearest(self, description: str, *, k: int) -> list[Neighbour]:
        if not self._examples or k <= 0:
            return []

        query = self._embedder.embed([description], kind="query")[0]

        scored = [
            Neighbour(example, _cosine(query, vector))
            for example, vector in zip(self._examples, self._vectors)
        ]
        # Sorted by score alone, with ties left in corpus order by `sorted` being
        # stable. Not `reverse=True` on a key of -score: the two differ exactly on
        # ties, and a lookup whose answer depends on how ties were spelt is a
        # lookup that changes when nothing changed.
        scored.sort(key=lambda n: n.score, reverse=True)
        return scored[:k]


class LexicalStore:
    """Character-trigram overlap. The control, and it may well win.

    No embedder, no key, no network, nothing to re-embed when a model changes --
    and on descriptions of two or three words it is not obviously worse. `linella`
    against `linella` is a perfect match here and merely a very good one under
    cosine; what it cannot do is know that `fidesco` and `kaufland` are both shops,
    which is precisely the thing embeddings are supposed to buy. #66 says the
    honest outcome may be "it did not help", and this class is what makes that
    sentence measurable rather than a shrug.

    Trigrams over words padded the way `pg_trgm` pads them, so that a later move of
    this logic into the database is a translation rather than a redesign.
    """

    def __init__(self, examples: Sequence[Example]) -> None:
        self._examples = list(examples)
        self._grams = [_trigrams(e.description) for e in self._examples]

    @property
    def label(self) -> str:
        return f"lexical(trigram, {len(self._examples)} rows)"

    def nearest(self, description: str, *, k: int) -> list[Neighbour]:
        if not self._examples or k <= 0:
            return []

        query = _trigrams(description)
        scored = [
            Neighbour(example, _jaccard(query, grams))
            for example, grams in zip(self._examples, self._grams)
        ]
        scored.sort(key=lambda n: n.score, reverse=True)

        # Zero-scoring rows are dropped here and nowhere else, and this is not the
        # threshold the `Neighbour` docstring refuses. A trigram score of exactly
        # zero means the two descriptions share no three consecutive characters at
        # all, so the row is not a weak match -- it is not a match, and it is only
        # in the list because k asked for five and the corpus had five. Cosine has
        # no equivalent: it never reaches zero between real strings, which is why
        # this line lives in this class rather than in `neighbours_for`.
        return [n for n in scored[:k] if n.score > 0.0]


def neighbours_for(
    store: ExampleStore | None, description: str, *, k: int
) -> list[Neighbour]:
    """The one door, and the only thing that catches.

    Broad on purpose, the way `AnthropicPredictor._category_for` is broad, and for
    the same reason: this sits on the path where a user's transaction is being
    saved, and #39's promise is that categorising can never cost that row. A
    retrieval that fails is a prompt with no examples, which is a predictor that
    already works.

    **The log line carries no description**, neither the query's nor any
    neighbour's. #64 made that rule for the model's call line and it holds harder
    here, because the whole content of this step is the user's own spending and a
    log is where it would sit for ever. The count, the timing and the best score
    are enough to tell "retrieval is working" from "retrieval is finding nothing"
    from "retrieval is broken"; `--show-examples` is where a human looks at the
    rows themselves, locally, on data they already have.
    """
    if store is None:
        return []

    started = time.perf_counter()

    try:
        found = store.nearest(description, k=k)
    except Exception:
        # `logger.exception` rather than `warning`, so the traceback survives: a
        # bug in the cosine and Voyage being unreachable are the same empty list
        # from outside, and the traceback is the only thing that tells them apart.
        logger.exception("Retrieval failed; categorising with no examples.")
        _log(store, "failed", started, [])
        return []

    _log(store, "found" if found else "empty", started, found)
    return found


def _log(
    store: ExampleStore, outcome: str, started: float, found: Sequence[Neighbour]
) -> None:
    logger.info(
        "retrieval outcome=%s store=%s count=%d elapsed_ms=%.0f top_score=%s",
        outcome,
        store.label,
        len(found),
        (time.perf_counter() - started) * 1000,
        # "none" rather than 0.0, for the reason #64 prints "unknown" rather than 0
        # for a missing token count: nothing found and a best match of zero are
        # different facts, and one of them would quietly become a zero in whatever
        # averages these.
        "none" if not found else f"{found[0].score:.3f}",
    )


def mode_from(env: Mapping[str, str]) -> str:
    """Which store to build, or `off`. #66's "turning retrieval off is one setting".

    An unrecognised value raises, which is `main.py`'s treatment of
    CATEGORIZER_PREDICTOR rather than `embedding.py`'s treatment of a mistyped
    timeout, and the difference is the same one written there: which way the
    mistake points. `CATEGORIZER_RETRIEVAL=vectors` -- the plural, which is the
    typo somebody will actually make -- would serve **no** retrieval while the
    deployment believed it had some, and the eval would then record a
    with-retrieval number produced without it. That is the one outcome this issue
    cannot survive, so it stops the process instead.

    Blank reads as `off`, for the reason blank reads as `rules` in `main.py`: an
    empty compose variable and an unset one arrive identically, and neither means
    "refuse to start".
    """
    wanted = env.get(RETRIEVAL_ENV, "").strip().lower()

    if not wanted:
        return "off"

    if wanted not in MODES:
        raise ValueError(
            f"{RETRIEVAL_ENV} is {wanted!r}; it has to be one of {', '.join(MODES)}."
        )

    return wanted


def example_count_from(env: Mapping[str, str]) -> int:
    """How many neighbours reach the prompt. Falls back rather than refusing.

    Unlike the mode above, and for the reason `_timeout_from` falls back: a wrong
    count changes how good the answer is, where a wrong mode changes what the
    number means.
    """
    raw = env.get(EXAMPLE_COUNT_ENV, "").strip()
    if not raw:
        return DEFAULT_EXAMPLE_COUNT

    try:
        return int(raw)
    except ValueError:
        logger.error(
            "%s=%r is not a whole number, so the default of %s is used.",
            EXAMPLE_COUNT_ENV,
            raw,
            DEFAULT_EXAMPLE_COUNT,
        )
        return DEFAULT_EXAMPLE_COUNT


def _cosine(left: Sequence[float], right: Sequence[float]) -> float:
    """Cosine similarity, computed rather than assumed.

    Voyage's documentation says its embeddings are normalised to length one, which
    would make a plain dot product identical and faster. Dividing by the norms
    anyway costs two more loops over 1024 floats and buys independence from that
    promise -- it stays correct for a truncated Matryoshka vector, which is *not*
    normalised until somebody renormalises it, and for whatever a future embedder
    returns. The property being protected is that this function's name is true.
    """
    dot = sum(a * b for a, b in zip(left, right))
    left_norm = math.sqrt(sum(a * a for a in left))
    right_norm = math.sqrt(sum(b * b for b in right))

    if left_norm == 0.0 or right_norm == 0.0:
        # A zero vector has no direction, so it has no angle to anything. Returning
        # 0.0 rather than dividing keeps this from being the one line in the
        # retrieval path that can raise.
        return 0.0

    return dot / (left_norm * right_norm)


def _trigrams(text: str) -> frozenset[str]:
    """Lower-cased three-character windows, over words padded like `pg_trgm` pads.

    Two leading spaces and one trailing, per word, which is what Postgres's
    extension does -- so `lidl` yields `  l`, ` li`, `lid`, `idl`, `dl `. The
    padding is what makes short words comparable at all: without it a four-letter
    merchant name has two trigrams and a one-character typo destroys both.

    Splitting on anything that is not alphanumeric, so `nr1 water 6l` is three
    words and `lidl, centru` is two. Deliberately no other normalisation -- no
    stemming, no accent folding, no synonyms. A fold that exists only in the
    retrieval path is #65's sharpest trap in a new coat: it would improve this
    store and silently change what the recorded number describes.
    """
    cleaned = "".join(c if c.isalnum() else " " for c in text.lower())

    grams: set[str] = set()
    for word in cleaned.split():
        padded = "  " + word + " "
        grams.update(padded[i : i + 3] for i in range(len(padded) - 2))

    return frozenset(grams)


def _jaccard(left: frozenset[str], right: frozenset[str]) -> float:
    if not left or not right:
        return 0.0

    return len(left & right) / len(left | right)
