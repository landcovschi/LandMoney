"""The HTTP surface: one prediction endpoint and one health endpoint.

    uv run uvicorn categorizer.main:app --reload

FastAPI generates the OpenAPI document from the type annotations, so the schema
and the validation are the same declaration rather than two that have to agree
-- the thing `TransactionContracts.cs` achieves by hand with DataAnnotations
plus `ValidationFilter<T>`. `/docs` serves a browsable form for it, which is the
fastest way to try this by hand.
"""

from typing import Annotated

from fastapi import Depends, FastAPI
from pydantic import BaseModel

from categorizer.contracts import CategorizeRequest, CategorizeResponse
from categorizer.predictor import Predictor, RulesPredictor

app = FastAPI(
    title="LandMoney categorizer",
    version="0.1.0",
    summary="Suggests a category for one transaction. Rules only -- no model, by rule.",
)

# One instance, built at import time, because it holds nothing per-request and
# nothing that can fail: `RULES` is a module-level tuple of strings. The moment a
# predictor owns a client with a connection pool and a timeout, this becomes a
# lifespan-managed object instead, and `get_predictor` is the line that changes
# rather than every call site -- which is what the indirection buys.
_RULES_PREDICTOR = RulesPredictor()


def get_predictor() -> Predictor:
    """The seam. Overridden in tests, replaced by the model adapter later.

    FastAPI's `dependency_overrides` swaps this for a fake by identity, so a test
    substitutes a predictor without patching a module or starting a real one --
    the same job a `ServiceCollection` registration does for a .NET integration
    test, minus the container.
    """
    return _RULES_PREDICTOR


class Health(BaseModel):
    status: str


@app.get("/health")
def health() -> Health:
    """Liveness, and deliberately nothing more.

    It runs no prediction and touches no dependency. Anything this could check --
    that `rules` imports, that the enum built -- happens at import time, so a
    failure of it means uvicorn never bound the port and there is no endpoint to
    ask. A health check that exercises more than the process cannot distinguish
    "broken" from "busy", and this one gates `depends_on` in docker-compose,
    where a false negative stops the application from starting at all.
    """
    return Health(status="ok")


@app.post("/categorize")
def categorize(
    request: CategorizeRequest,
    predictor: Annotated[Predictor, Depends(get_predictor)],
) -> CategorizeResponse:
    """A description, an amount and a currency in; a category and its source out.

    A 200 with `category: null` is a normal answer, not an error: the rules
    abstain on roughly a third of the labelled set (#25 -- 16 of 17 misses are
    abstentions rather than confusions), and an abstention is the baseline
    working as designed. Answering 404 or 204 instead would make the caller's
    "no category" branch depend on which kind of nothing it was, and the .NET
    side treats both the same.

    No `async` on this handler, on purpose. `rules.predict` is a synchronous
    substring scan with no I/O in it, so declaring it `async def` would run it
    on the event loop thread and block every other request for its duration; a
    plain `def` is dispatched to a worker thread by Starlette instead. That is
    the opposite of the .NET instinct, where `async` is the safe default -- here
    `async` is a promise not to block, and this function cannot keep it.
    """
    return predictor.categorize(request)
