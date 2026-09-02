"""Tests for the batch endpoint and the fan-out behind it -- #93.

Two things are worth testing here and they are not the same thing. `answer_all`
is where a row can be lost or paired with the wrong id, and it is reachable with
no HTTP at all; the endpoint is where the request is bound, the cap is enforced
and the duplicate ids are refused. Neither opens a socket, and the fake predictor
below inherits from nothing -- which is the `Protocol` being structural, the same
point `test_api.py` makes about the single-row seam.

The load-bearing test is `test_an_answer_is_paired_with_the_row_it_answers`.
#93's last trap is a batch that answers positionally and silently drops a row,
which shows up as one transaction categorised as its neighbour -- so the fake
below deliberately finishes *out of order* and answers a different category per
description, which is the only arrangement in which a positional implementation
would be caught rather than pass by luck.
"""

import threading
import time
from decimal import Decimal

import pytest
from fastapi.testclient import TestClient

from categorizer.batch import (
    DEFAULT_CONCURRENCY,
    MAX_CONCURRENCY,
    answer_all,
    concurrency_from_env,
)
from categorizer.contracts import (
    MAX_BATCH_ITEMS,
    BatchItem,
    CategorizeRequest,
    CategorizeResponse,
    Category,
    Source,
)
from categorizer.main import app, get_predictor


def item(id: str, description: str, amount: str = "12.34", currency: str = "EUR") -> BatchItem:
    return BatchItem(id=id, description=description, amount=Decimal(amount), currency=currency)


def body(*items: BatchItem) -> dict:
    """The same rows as a JSON body, so a test writes its rows once."""
    return {
        "items": [
            {
                "id": one.id,
                "description": one.description,
                "amount": str(one.amount),
                "currency": one.currency,
            }
            for one in items
        ]
    }


class ByDescription:
    """Answers the description back as the category, so an answer names its own row.

    Inherits nothing, which is what proves the structural typing. The optional
    delays are what make the ordering test mean something: with them, the rows
    finish in the reverse of the order they were submitted.
    """

    def __init__(self, delays: dict[str, float] | None = None):
        self.delays = delays or {}
        self.seen: list[str] = []
        self.lock = threading.Lock()

    def categorize(self, request: CategorizeRequest) -> CategorizeResponse:
        time.sleep(self.delays.get(request.description, 0))

        with self.lock:
            self.seen.append(request.description)

        return CategorizeResponse(category=Category(request.description), source=Source.MODEL)


# --- the fan-out --------------------------------------------------------------


def test_an_answer_is_paired_with_the_row_it_answers():
    """#93's last trap: the answers are keyed by the caller's id, never by position."""
    rows = [item("first", "groceries"), item("second", "transport"), item("third", "health")]

    # The first row is the slowest, so it finishes last. An implementation that
    # collected completions in the order they arrived would hand "first" the answer
    # that belongs to "third".
    predictor = ByDescription({"groceries": 0.05, "transport": 0.02})

    answers = answer_all(predictor, rows, concurrency=3)

    assert [(answer.id, answer.category) for answer in answers] == [
        ("first", Category.GROCERIES),
        ("second", Category.TRANSPORT),
        ("third", Category.HEALTH),
    ]

    # And they really did finish out of order, or the assertion above proves nothing.
    assert predictor.seen == ["health", "transport", "groceries"]


def test_every_row_is_asked_about_exactly_once():
    rows = [item(str(index), "groceries") for index in range(20)]
    predictor = ByDescription()

    answers = answer_all(predictor, rows, concurrency=4)

    assert len(answers) == 20
    assert len(predictor.seen) == 20
    assert sorted(answer.id for answer in answers) == sorted(str(index) for index in range(20))


def test_the_rows_are_asked_about_concurrently():
    """The whole point of the endpoint, asserted as arithmetic rather than as a feeling.

    Eight rows that each take 100 ms are 800 ms one at a time and about 200 ms at a
    concurrency of four. The bound is deliberately loose -- half the sequential
    time -- because a test that pins a wall clock on a shared runner goes red for
    reasons that have nothing to do with this code.
    """
    rows = [item(str(index), "groceries") for index in range(8)]
    predictor = ByDescription({"groceries": 0.1})

    started = time.perf_counter()
    answer_all(predictor, rows, concurrency=4)
    elapsed = time.perf_counter() - started

    assert elapsed < 0.4


def test_a_row_that_raises_is_left_out_and_the_rest_are_answered():
    """One bad row must not cost the others their answers, which were already paid for."""

    class RaisesOnOne:
        def categorize(self, request: CategorizeRequest) -> CategorizeResponse:
            if request.description == "transport":
                raise RuntimeError("the adapter has a bug")

            return CategorizeResponse(category=Category(request.description), source=Source.MODEL)

    rows = [item("a", "groceries"), item("b", "transport"), item("c", "health")]

    answers = answer_all(RaisesOnOne(), rows, concurrency=3)

    # The caller sees an id it sent and did not get back, which is the signal it
    # needs in order to ask again. Padding it with a null would have said "asked,
    # and no idea" -- an abstention, which is a final answer, and the wrong one.
    assert [answer.id for answer in answers] == ["a", "c"]


def test_no_rows_is_no_answers():
    assert answer_all(ByDescription(), [], concurrency=4) == []


def test_one_row_is_answered_although_the_pool_is_allowed_to_be_larger():
    answers = answer_all(ByDescription(), [item("a", "groceries")], concurrency=8)

    assert [answer.id for answer in answers] == ["a"]


# --- the concurrency setting --------------------------------------------------


@pytest.mark.parametrize("raw", ["", "   "])
def test_an_unset_concurrency_is_the_default(raw: str):
    assert concurrency_from_env({"CATEGORIZER_BATCH_CONCURRENCY": raw}) == DEFAULT_CONCURRENCY
    assert concurrency_from_env({}) == DEFAULT_CONCURRENCY


def test_a_concurrency_that_is_not_a_number_falls_back_rather_than_raising():
    """Deliberately unlike CATEGORIZER_PREDICTOR, which refuses to start at all.

    The difference is what each mistake costs. A wrong predictor serves the rules
    while the deployment believes a model is running, which is a number recorded
    under the wrong name; a wrong thread count makes a batch slower and says so in
    the line this module logs.
    """
    assert concurrency_from_env({"CATEGORIZER_BATCH_CONCURRENCY": "eight"}) == DEFAULT_CONCURRENCY


@pytest.mark.parametrize(
    ("raw", "expected"),
    [("1", 1), ("4", 4), ("0", 1), ("-3", 1), ("500", MAX_CONCURRENCY)],
)
def test_a_concurrency_outside_the_range_is_clamped(raw: str, expected: int):
    assert concurrency_from_env({"CATEGORIZER_BATCH_CONCURRENCY": raw}) == expected


# --- the endpoint -------------------------------------------------------------


@pytest.fixture
def client():
    """The app with a predictor that names the row it answered."""
    app.dependency_overrides[get_predictor] = lambda: ByDescription()
    yield TestClient(app)
    app.dependency_overrides.clear()


def test_the_endpoint_answers_one_row_per_item(client: TestClient):
    response = client.post(
        "/categorize/batch", json=body(item("a", "groceries"), item("b", "transport"))
    )

    assert response.status_code == 200
    assert response.json() == {
        "answers": [
            {"id": "a", "category": "groceries", "source": "model"},
            {"id": "b", "category": "transport", "source": "model"},
        ]
    }


def test_the_batch_endpoint_and_the_single_endpoint_answer_the_same_thing():
    """The two share a contract by inheritance; this is what says so out loud.

    `BatchItem` is a `CategorizeRequest` and `BatchAnswer` is a
    `CategorizeResponse`, so a rule that holds for one row holds for a hundred --
    and the day somebody writes a second parser for the batch path, this goes red.
    It runs against the real rules rather than a fake, for the reason
    `test_the_endpoint_answers_exactly_what_the_rules_do` does.
    """
    plain = TestClient(app)
    descriptions = ["coffee at the cafe", "Lidl", "bus ticket", "pharmacy"]

    one_at_a_time = [
        plain.post(
            "/categorize",
            json={"description": description, "amount": "12.34", "currency": "EUR"},
        ).json()
        for description in descriptions
    ]

    together = plain.post(
        "/categorize/batch",
        json=body(*(item(description, description) for description in descriptions)),
    ).json()

    assert [
        {"category": answer["category"], "source": answer["source"]}
        for answer in together["answers"]
    ] == one_at_a_time


def test_an_empty_batch_is_refused(client: TestClient):
    """A request that asks nothing is a caller with a bug, not a batch of zero."""
    assert client.post("/categorize/batch", json={"items": []}).status_code == 422


def test_a_batch_over_the_cap_is_refused(client: TestClient):
    rows = [item(str(index), "groceries") for index in range(MAX_BATCH_ITEMS + 1)]

    assert client.post("/categorize/batch", json=body(*rows)).status_code == 422


def test_a_batch_exactly_at_the_cap_is_accepted(client: TestClient):
    """The cap is a limit, not a fence one short of it."""
    rows = [item(str(index), "groceries") for index in range(MAX_BATCH_ITEMS)]

    response = client.post("/categorize/batch", json=body(*rows))

    assert response.status_code == 200
    assert len(response.json()["answers"]) == MAX_BATCH_ITEMS


def test_repeated_ids_are_refused_and_the_message_names_them(client: TestClient):
    """There is no unambiguous answer to two rows under one key, so there is no answer."""
    response = client.post(
        "/categorize/batch", json=body(item("a", "groceries"), item("a", "transport"))
    )

    assert response.status_code == 422
    assert "'a'" in response.text


def test_a_row_that_breaks_the_per_row_rules_refuses_the_whole_batch(client: TestClient):
    """The same validation as `POST /categorize`, because it is the same declaration.

    Whole-request rather than per-row, which is the opposite of what the .NET
    import does with a CSV and is right for a different reason: a caller sending a
    negative amount has a bug, where a person writing a CSV has a typo. FastAPI
    names the offending index either way, which is what a caller with a bug needs.
    """
    rows = body(item("a", "groceries"), item("b", "transport"))
    rows["items"][1]["amount"] = "-1.00"

    assert client.post("/categorize/batch", json=rows).status_code == 422


def test_an_id_is_required(client: TestClient):
    rows = body(item("a", "groceries"))
    del rows["items"][0]["id"]

    assert client.post("/categorize/batch", json=rows).status_code == 422
