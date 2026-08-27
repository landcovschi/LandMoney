"""Tests for the HTTP surface -- #39.

In the spirit of #21's mutation sweep: a test that cannot fail is decoration.
The load-bearing one here is `test_the_endpoint_answers_exactly_what_the_rules_do`,
which is the whole justification for moving `rules.py` out of `evals/` -- if the
endpoint can disagree with the scored function, the baseline is a number about
something that is not deployed.

`TestClient` runs the ASGI app in-process with no socket and no uvicorn, the way
`WebApplicationFactory` runs a .NET app -- except that here it costs one dev
dependency (`httpx2`) rather than the package #21 decided to keep out. That
changes the calculation #21 made: the reason `Microsoft.AspNetCore.Mvc.Testing`
stayed out was that an `IEndpointFilter` is reachable without a server, and here
the routing, the 422 and the dependency override are the things under test, so
there is nothing left to test if the server is taken away.
"""

from decimal import Decimal

import pytest
from fastapi.testclient import TestClient

from categorizer.categories import CATEGORIES, NO_PREDICTION
from categorizer.contracts import CategorizeRequest, CategorizeResponse, Category, Source
from categorizer.main import app, build_predictor, get_predictor
from categorizer.predictor import RulesPredictor
from categorizer.rules import RULES, predict as predict_by_rules


@pytest.fixture
def client() -> TestClient:
    """The app as it is wired in production -- the real RulesPredictor."""
    return TestClient(app)


def post(client: TestClient, description: str, amount: str = "12.34", currency: str = "EUR"):
    return client.post(
        "/categorize",
        json={"description": description, "amount": amount, "currency": currency},
    )


# --- the equivalence that justifies the move ---------------------------------


def test_the_endpoint_answers_exactly_what_the_rules_do(client: TestClient):
    """For all 109 rules: the API and `rules.predict` agree, sentinel aside.

    Every needle is posted as its own description, so the ordering collisions are
    exercised as they really resolve rather than as anyone remembers them -- the
    expectation is computed by calling `predict`, not typed out, so this test
    knows nothing about which rule wins and cannot drift from the baseline.
    """
    for needle, _ in RULES:
        expected = predict_by_rules(needle)
        response = post(client, needle)

        assert response.status_code == 200, needle
        body = response.json()
        assert body["source"] == "rules", needle
        if expected == NO_PREDICTION:
            assert body["category"] is None, needle
        else:
            assert body["category"] == expected, needle


def test_the_vocabulary_the_api_can_serve_is_the_vocabulary_the_scorer_knows():
    """The enum is built from CATEGORIES, so this fails if anyone retypes it."""
    assert tuple(member.value for member in Category) == CATEGORIES


# --- the two normal answers --------------------------------------------------


def test_a_matched_description_comes_back_categorised(client: TestClient):
    body = post(client, "Dinner at the pizza place").json()
    assert body == {"category": "eating-out", "source": "rules"}


def test_an_unmatched_description_is_a_200_with_a_null_category(client: TestClient):
    """Abstention is a normal answer, not an error, and never the sentinel.

    "Blood tests" is one of the real misses from #25's run: 16 of the 17 are
    abstentions rather than confusions, so this is the baseline's commonest
    non-answer rather than an invented one.
    """
    response = post(client, "Blood tests")

    assert response.status_code == 200
    assert response.json() == {"category": None, "source": "rules"}
    # The sentinel must not survive the boundary: categories.py keeps it outside
    # the vocabulary so the scorer counts it as a miss, and the .NET column would
    # store the string quite happily.
    assert NO_PREDICTION not in response.text


# --- the contract ------------------------------------------------------------


def test_an_ordinary_amount_is_not_parsed_through_a_float(client: TestClient):
    """12.34 is accepted, and that acceptance is the measurement.

    As a float, 12.34 is 12.339999999999999857891452847979962825775146484375 --
    fifty decimal places, so `decimal_places=2` would reject it. A pass here
    means pydantic handed the JSON token straight to Decimal.
    """
    assert post(client, "Coffee", amount="12.34").status_code == 200
    assert CategorizeRequest(description="x", amount=Decimal("12.34"), currency="EUR")


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("amount", "12.345"),  # numeric(18,2) rounds this away silently; refuse it
        ("amount", "0"),
        ("amount", "-1.00"),
        ("currency", "EU"),
        ("currency", "1$x"),  # a length check alone would let this through
        ("description", ""),
    ],
)
def test_a_body_outside_the_contract_is_a_422(client: TestClient, field: str, value: str):
    body = {"description": "Coffee", "amount": "12.34", "currency": "EUR"} | {field: value}
    assert client.post("/categorize", json=body).status_code == 422


def test_a_missing_field_is_a_422(client: TestClient):
    assert client.post("/categorize", json={"description": "Coffee"}).status_code == 422


# --- the seam ----------------------------------------------------------------


class FakePredictor:
    """Implements `Predictor` by shape alone -- it inherits nothing and imports
    nothing from `predictor.py`. That is `Protocol` being structural, and it is
    what the model adapter will rely on too."""

    def categorize(self, request: CategorizeRequest) -> CategorizeResponse:
        return CategorizeResponse(category=Category.OTHER, source=Source.MODEL)


def test_the_predictor_can_be_replaced_without_touching_the_endpoint():
    """`dependency_overrides` is the extension point a later issue plugs into."""
    app.dependency_overrides[get_predictor] = FakePredictor
    try:
        with TestClient(app) as client:
            # "Dinner" is a rule; the real predictor would answer eating-out.
            assert post(client, "Dinner").json() == {"category": "other", "source": "model"}
    finally:
        # A leaked override changes every test that runs after this one, in file
        # order, which is the worst kind of failure to read.
        app.dependency_overrides.clear()


# --- which predictor is behind the port -- #59 -------------------------------


def test_nothing_configured_is_the_rules():
    """The default has to stay the baseline: `docker compose up`, a fresh clone and
    every CI run set nothing, and none of them should spend money."""
    assert isinstance(build_predictor({}), RulesPredictor)


@pytest.mark.parametrize("value", ["rules", "RULES", " rules ", "", "   "])
def test_the_rules_are_the_answer_to_blank_and_to_their_own_name(value: str):
    """Blank included deliberately: `${CATEGORIZER_PREDICTOR:-}` in a compose file
    and a Container Apps variable set to nothing both arrive as an empty string, and
    neither means "refuse to start"."""
    assert isinstance(build_predictor({"CATEGORIZER_PREDICTOR": value}), RulesPredictor)


@pytest.mark.parametrize("value", ["modle", "anthropic", "claude", "opus", "true"])
def test_an_unrecognised_predictor_refuses_to_start(value: str):
    """**The most valuable test in this file**, because of which way the mistake
    points.

    A typo that fell back to the rules would serve the baseline while the deployment
    believed a model was running, and #60 would record that number as a model
    result. Nothing would report it -- the service is healthy, the answers are
    plausible, the score is simply the old one. A container that will not start says
    so in one line.
    """
    with pytest.raises(ValueError, match="CATEGORIZER_PREDICTOR"):
        build_predictor({"CATEGORIZER_PREDICTOR": value})


def test_a_canned_model_answer_reaches_the_client_unchanged():
    """The seam end to end: a predictor naming itself `model` is served as `model`.

    Canned, through `dependency_overrides`, so this costs nothing and needs no key --
    and it is what the .NET side reads to write `transactions.category_source`.
    """
    app.dependency_overrides[get_predictor] = FakePredictor
    try:
        with TestClient(app) as client:
            assert post(client, "anything at all").json() == {
                "category": "other",
                "source": "model",
            }
    finally:
        app.dependency_overrides.clear()


# --- health ------------------------------------------------------------------


def test_health_is_ok(client: TestClient):
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}
