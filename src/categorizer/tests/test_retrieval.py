"""Retrieval -- #66. No network, no database, no vendor.

`VectorStore` takes its embedder as a constructor argument and `neighbours_for`
takes its store as one, so a test that forgot to substitute either fails to
construct rather than quietly reaching Voyage. `FakeEmbedder` inherits nothing,
which is `Embedder` being a structural Protocol -- the third time in this package.

**The vectors here are hand-written, not embeddings.** A test that asked a real
model whether `fidesco` is near `linella` would be measuring the model, cost money,
and answer differently next year. What is being tested is that the nearest vector
wins, that the corpus and its vectors stay paired, and that nothing in this file
can raise on the save path; whether the vectors are any good is `evals/score.py`'s
question and it is answered in dollars.
"""

import logging

import pytest

from categorizer.retrieval import (
    DEFAULT_EXAMPLE_COUNT,
    EXAMPLE_COUNT_ENV,
    RETRIEVAL_ENV,
    Example,
    LexicalStore,
    Neighbour,
    VectorStore,
    _cosine,
    _jaccard,
    _trigrams,
    example_count_from,
    mode_from,
    neighbours_for,
)

CORPUS = [
    Example("linella", "groceries"),
    Example("kaufland", "groceries"),
    Example("rent july", "housing"),
    Example("bolt ride", "transport"),
]


class FakeEmbedder:
    """Vectors from a table, and a record of how each text was asked for.

    `asked` is the point of the class rather than a convenience: `kind` is the
    parameter `embedding.py` says fails silently, and the only way to catch a
    corpus embedded as queries is to look at what was requested.
    """

    def __init__(self, table: dict[str, list[float]], dimensions: int = 3) -> None:
        self._table = table
        self._dimensions = dimensions
        self.asked: list[tuple[tuple[str, ...], str]] = []

    @property
    def model(self) -> str:
        return "fake-embedder"

    @property
    def dimensions(self) -> int:
        return self._dimensions

    def embed(self, texts, *, kind):
        self.asked.append((tuple(texts), kind))
        return [self._table[text] for text in texts]


class ExplodingStore:
    @property
    def label(self) -> str:
        return "exploding"

    def nearest(self, description: str, *, k: int):
        raise RuntimeError("the database is not there")


# --- the lexical control -----------------------------------------------------


def test_an_exact_description_is_the_nearest_neighbour() -> None:
    found = LexicalStore(CORPUS).nearest("linella", k=2)

    assert found[0].example == Example("linella", "groceries")
    assert found[0].score == 1.0


def test_rows_sharing_no_three_characters_are_dropped() -> None:
    """Not the score floor the `Neighbour` docstring refuses.

    A trigram score of exactly zero is not a weak match, it is the absence of one:
    the row is in the list only because k asked for four and the corpus had four.
    Cosine has no equivalent -- it never reaches zero between real strings -- which
    is why this lives in `LexicalStore` and not in `neighbours_for`.
    """
    found = LexicalStore(CORPUS).nearest("zzz", k=4)

    assert found == []


def test_the_lexical_store_needs_no_embedder_at_all() -> None:
    """The whole argument for the control being a control.

    It is constructed from the corpus and nothing else, so scoring it costs no
    vendor, no key and no network -- which is what makes "did embeddings actually
    help" a question with a free answer.
    """
    store = LexicalStore(CORPUS)

    assert "lexical" in store.label
    assert store.nearest("linella centru", k=1)[0].example.category == "groceries"


# --- the vector store --------------------------------------------------------


def vectors_for(**table: list[float]) -> FakeEmbedder:
    return FakeEmbedder({name.replace("_", " "): vector for name, vector in table.items()})


def test_the_nearest_vector_wins_whatever_the_words_say() -> None:
    """The property a lexical store structurally cannot have.

    `fidesco` shares no trigram with `linella`, so `LexicalStore` scores it zero and
    drops it. Here it is placed next to the groceries rows by its vector alone,
    which is the entire thing #66 is buying and the entire reason it might not be
    worth it.
    """
    embedder = vectors_for(
        linella=[1.0, 0.0, 0.0],
        kaufland=[0.9, 0.1, 0.0],
        rent_july=[0.0, 1.0, 0.0],
        bolt_ride=[0.0, 0.0, 1.0],
        fidesco=[0.95, 0.05, 0.0],
    )
    store = VectorStore.build(CORPUS, embedder)

    found = store.nearest("fidesco", k=2)

    assert [n.example.description for n in found] == ["linella", "kaufland"]
    assert all(n.example.category == "groceries" for n in found)


def test_the_corpus_is_embedded_as_documents_and_the_query_as_a_query() -> None:
    """The parameter that degrades retrieval without failing it.

    Voyage prepends a different sentence to each, so a corpus embedded as queries
    still returns neighbours, ranked worse, with nothing anywhere reporting it.
    These are the only two places in the package where that word is chosen and this
    is the only test that looks at either.
    """
    embedder = vectors_for(
        linella=[1.0, 0.0, 0.0],
        kaufland=[0.0, 1.0, 0.0],
        rent_july=[0.0, 0.0, 1.0],
        bolt_ride=[1.0, 1.0, 0.0],
        espresso=[0.5, 0.5, 0.0],
    )
    store = VectorStore.build(CORPUS, embedder)
    store.nearest("espresso", k=1)

    corpus_call, query_call = embedder.asked
    assert corpus_call == (("linella", "kaufland", "rent july", "bolt ride"), "document")
    assert query_call == (("espresso",), "query")


def test_the_corpus_is_embedded_in_one_request() -> None:
    embedder = vectors_for(
        linella=[1.0, 0.0, 0.0],
        kaufland=[0.0, 1.0, 0.0],
        rent_july=[0.0, 0.0, 1.0],
        bolt_ride=[1.0, 1.0, 0.0],
    )
    VectorStore.build(CORPUS, embedder)

    assert len(embedder.asked) == 1


def test_examples_and_vectors_that_do_not_pair_up_are_refused() -> None:
    """Silent and permanent otherwise.

    A mismatch pairs every example with the wrong vector, and the only symptom is
    retrieval returning unrelated rows -- the same failure `embed`'s index sort
    exists to prevent, one layer up, which is why it is refused in both places.
    """
    with pytest.raises(ValueError):
        VectorStore(CORPUS, [[1.0, 0.0, 0.0]], FakeEmbedder({}))


def test_the_label_names_the_model_and_the_corpus_size() -> None:
    """#66's last trap: changing the embedding model invalidates every vector.

    A recorded score has to say which model produced the vectors it retrieved with,
    or a later run against a different one is not a comparison. The store is asked
    rather than the setting, so the label cannot claim a model that did not answer.
    """
    embedder = vectors_for(
        linella=[1.0, 0.0, 0.0],
        kaufland=[0.0, 1.0, 0.0],
        rent_july=[0.0, 0.0, 1.0],
        bolt_ride=[1.0, 1.0, 0.0],
    )

    label = VectorStore.build(CORPUS, embedder).label

    assert "fake-embedder" in label
    assert "4 rows" in label


# --- the one door ------------------------------------------------------------


def test_no_store_is_no_neighbours_and_no_call() -> None:
    assert neighbours_for(None, "anything", k=5) == []


def test_a_store_that_raises_is_an_empty_list_and_never_an_exception() -> None:
    """#39's promise, arriving at a third dependency.

    This runs on the path where a user's transaction is being saved. A database
    that is down, a Voyage that times out and a bug in the cosine must all be a
    prompt with no examples -- which is the predictor #60 measured at 98.9% -- and
    never a failed save.
    """
    assert neighbours_for(ExplodingStore(), "linella", k=5) == []


def test_the_log_line_carries_no_description(caplog) -> None:
    """#64's rule, and it holds harder here than it did there.

    The whole content of this step is the user's own spending: the query is one
    description and every neighbour is another. A log is where those would sit for
    ever. The count, the timing and the best score separate "working" from "finding
    nothing" from "broken", which is all an operator needs; `--show-examples` is
    where a human looks at the rows, locally, on data they already have.
    """
    with caplog.at_level(logging.INFO, logger="categorizer.retrieval"):
        neighbours_for(LexicalStore(CORPUS), "linella", k=2)

    logged = "\n".join(record.getMessage() for record in caplog.records)
    assert "retrieval outcome=found" in logged
    assert "linella" not in logged
    assert "kaufland" not in logged
    assert "groceries" not in logged


def test_a_failure_and_an_empty_result_are_different_lines(caplog) -> None:
    """Per #64: an absence that is not counted is an absence nobody learns about.

    "The corpus had nothing similar" and "retrieval is broken" are the same empty
    prompt and want opposite reactions, so they must not be the same word in the
    log.
    """
    with caplog.at_level(logging.INFO, logger="categorizer.retrieval"):
        neighbours_for(LexicalStore(CORPUS), "zzz", k=2)
        neighbours_for(ExplodingStore(), "zzz", k=2)

    logged = "\n".join(record.getMessage() for record in caplog.records)
    assert "outcome=empty" in logged
    assert "outcome=failed" in logged


# --- configuration -----------------------------------------------------------


@pytest.mark.parametrize("value", ["", "  "])
def test_a_blank_setting_is_off(value: str) -> None:
    assert mode_from({RETRIEVAL_ENV: value}) == "off"


@pytest.mark.parametrize("value, expected", [("off", "off"), ("LEXICAL", "lexical"), (" vector ", "vector")])
def test_the_three_modes_are_recognised(value: str, expected: str) -> None:
    assert mode_from({RETRIEVAL_ENV: value}) == expected


def test_an_unrecognised_mode_stops_the_process() -> None:
    """Deliberately unlike the timeout and the example count, which fall back.

    `vectors` -- the plural, which is the typo somebody will actually make -- would
    serve no retrieval while the deployment believed it had some, and #66 would then
    record a with-retrieval number produced without any. That is the one outcome
    this issue cannot survive, so it is the one setting that refuses.
    """
    with pytest.raises(ValueError):
        mode_from({RETRIEVAL_ENV: "vectors"})


@pytest.mark.parametrize(
    "value, expected", [("", DEFAULT_EXAMPLE_COUNT), ("3", 3), ("lots", DEFAULT_EXAMPLE_COUNT)]
)
def test_the_example_count_falls_back_rather_than_refusing(value: str, expected: int) -> None:
    assert example_count_from({EXAMPLE_COUNT_ENV: value}) == expected


# --- the arithmetic ----------------------------------------------------------


def test_trigrams_are_padded_the_way_pg_trgm_pads_them() -> None:
    """Two leading spaces and one trailing, per word.

    The padding is what makes short words comparable at all: without it a
    four-letter merchant name has two trigrams and one typo destroys both.
    """
    assert _trigrams("lidl") == frozenset({"  l", " li", "lid", "idl", "dl "})


def test_punctuation_splits_words_and_nothing_else_is_normalised() -> None:
    """No stemming, no accent folding, no synonyms.

    A fold that exists only in the retrieval path is #65's sharpest trap in a new
    coat -- it would improve this store and silently change what the recorded
    number describes.
    """
    assert _trigrams("lidl, centru") == _trigrams("LIDL CENTRU")


def test_cosine_is_computed_rather_than_assuming_unit_vectors() -> None:
    """Voyage promises normalised vectors; this does not rely on the promise.

    A truncated Matryoshka vector is not normalised until somebody renormalises it,
    and a dot product would silently rank by magnitude instead of by angle. The
    property being protected is that the function's name is true.
    """
    assert _cosine([2.0, 0.0], [7.0, 0.0]) == pytest.approx(1.0)
    assert _cosine([1.0, 0.0], [0.0, 1.0]) == pytest.approx(0.0)
    assert _cosine([0.0, 0.0], [1.0, 1.0]) == 0.0


def test_jaccard_is_the_overlap_over_the_union() -> None:
    assert _jaccard(frozenset("ab"), frozenset("ab")) == 1.0
    assert _jaccard(frozenset("ab"), frozenset("bc")) == pytest.approx(1 / 3)
    assert _jaccard(frozenset(), frozenset("ab")) == 0.0


def test_a_neighbour_is_an_example_and_a_score() -> None:
    neighbour = Neighbour(Example("linella", "groceries"), 0.5)

    assert neighbour.example.category == "groceries"
    assert neighbour.score == 0.5
