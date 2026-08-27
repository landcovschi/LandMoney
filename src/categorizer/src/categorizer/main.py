"""The HTTP surface: one prediction endpoint and one health endpoint.

    uv run uvicorn categorizer.main:app --reload

FastAPI generates the OpenAPI document from the type annotations, so the schema
and the validation are the same declaration rather than two that have to agree
-- the thing `TransactionContracts.cs` achieves by hand with DataAnnotations
plus `ValidationFilter<T>`. `/docs` serves a browsable form for it, which is the
fastest way to try this by hand.
"""

import logging
import os
from typing import Annotated, Mapping

from fastapi import Depends, FastAPI
from pydantic import BaseModel

from categorizer.contracts import CategorizeRequest, CategorizeResponse
from categorizer.predictor import Predictor, RulesPredictor

logger = logging.getLogger(__name__)

app = FastAPI(
    title="LandMoney categorizer",
    version="0.1.0",
    summary="Suggests a category for one transaction. Rules only -- no model, by rule.",
)

def build_predictor(env: Mapping[str, str]) -> Predictor:
    """Which implementation is behind the port -- #59, and the switch is one word.

    Rules unless told otherwise, so nothing changes for anyone who sets nothing:
    `docker compose up`, `pytest` and a fresh clone all get the baseline, and the
    model costs money only when it is asked for by name.

    **An unrecognised value raises, and the process does not start.** That is
    deliberately the opposite of how `Categorizer:BaseUrl` behaves on the .NET side,
    where an absent value is a legal state -- and the reason for the difference is
    which way the mistake points. There, the missing key had one unavoidable cause
    (`efbundle` runs `Program.cs` with no `appsettings.json`) and the failure was a
    deploy that died. Here a typo -- `modle`, `Model `, `anthropic` -- would serve
    the *rules* while the deployment believed a model was running, and #60 would
    then record a number under the wrong name with nothing anywhere reporting it. A
    container that will not start says so in one line; a baseline mislabelled as a
    model result is discovered months later, if at all.

    Takes the environment as an argument rather than reading `os.environ` itself, so
    a test names the configuration it is testing instead of mutating the process.
    """
    wanted = env.get("CATEGORIZER_PREDICTOR", "").strip().lower()

    # Blank reads as unset, and that is worth a line because it disagrees with how
    # `Authentication:InviteCode` is read on the .NET side, where empty means "fail
    # closed". The difference is which way each default points: there the safe state
    # is refusing, here the safe state is the free one. It also removes a foot-gun,
    # since `${CATEGORIZER_PREDICTOR:-}` in a compose file or a Container Apps
    # environment variable set to nothing both arrive as an empty string, and
    # refusing to start over that would be a puzzle rather than a signal.
    if not wanted or wanted == "rules":
        return RulesPredictor()

    if wanted == "model":
        # Imported here, not at module scope, so that the `anthropic` package is
        # only needed by a process that actually asked for the model -- and so the
        # rules path keeps starting with nothing installed beyond FastAPI.
        from categorizer.anthropic_predictor import AnthropicPredictor

        logger.info("Categorising with the model. This costs money per request.")
        return AnthropicPredictor.from_env(env)

    raise ValueError(
        f"CATEGORIZER_PREDICTOR is {wanted!r}; it has to be 'rules' or 'model'."
    )


# Built once at import. #39 predicted this would become lifespan-managed the moment
# a predictor owned a connection pool, and it now does -- the argument is written
# down here rather than acted on, because the lifespan version costs more than it
# buys at this size. There is nothing to release that ending the process does not
# release, this service is one container per predictor, and `TestClient(app)`
# outside a `with` block never runs a lifespan -- so every existing test would have
# to change to reach a seam none of them use. It becomes the right answer when
# something here needs shutting down cleanly, or when the predictor has to change
# without a restart.
_PREDICTOR = build_predictor(os.environ)


def get_predictor() -> Predictor:
    """The seam. Overridden in tests; chosen by configuration in production.

    FastAPI's `dependency_overrides` swaps this for a fake by identity, so a test
    substitutes a predictor without patching a module or starting a real one --
    the same job a `ServiceCollection` registration does for a .NET integration
    test, minus the container.
    """
    return _PREDICTOR


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
