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
import time
from typing import Annotated, Mapping

from fastapi import Depends, FastAPI
from pydantic import BaseModel

from categorizer.batch import CONCURRENCY, answer_all
from categorizer.contracts import (
    CategorizeBatchRequest,
    CategorizeBatchResponse,
    CategorizeRequest,
    CategorizeResponse,
)
from categorizer.predictor import Predictor, RulesPredictor

logger = logging.getLogger(__name__)

# **Without this line, nothing this package logs is ever written down** -- found by
# #93 while checking that its own summary line reached the container's output, and
# it did not.
#
# uvicorn calls `dictConfig` with its own configuration, which declares the
# `uvicorn`, `uvicorn.error` and `uvicorn.access` loggers and **no root logger**
# (read out of `uvicorn.config.LOGGING_CONFIG` in the running container rather than
# remembered). A logger like `categorizer.main` therefore propagates to a root that
# has no handler, where `logging.lastResort` drops anything under WARNING. So every
# INFO line in this service was going nowhere: #64's `model_call`, which is how many
# model calls were billed, and #65's `cache`, which is the running hit rate. Both
# were described as "the durable record" for a container that scales to zero, and
# both were silent in the one place that matters.
#
# `basicConfig` adds a handler to the root logger and nothing else. It cannot
# duplicate uvicorn's own output, because `uvicorn` and `uvicorn.access` both set
# `propagate: false` -- checked in the same dump, not assumed -- and it is a no-op
# when a handler is already installed, which is what makes it safe under pytest.
#
# The format matches uvicorn's default deliberately, so one stream does not read as
# two. The logger name is left out for the same reason it is left out there: every
# line this package writes begins with a word saying what it is -- `model_call`,
# `cache`, `batch` -- which is what a parser would key on anyway.
#
# A full `dictConfig` is the alternative and is what #64 declined for the .NET-side
# reason it gives: uvicorn owns this process's logging, and reformatting every line
# the server writes is a bigger change than making this package's lines appear.
logging.basicConfig(level=logging.INFO, format="%(levelname)s:     %(message)s")

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


@app.post("/categorize/batch")
def categorize_batch(
    request: CategorizeBatchRequest,
    predictor: Annotated[Predictor, Depends(get_predictor)],
) -> CategorizeBatchResponse:
    """Many rows in, many answers out, one round trip -- #93.

    The endpoint #62 named as the honest fix and deferred: an import stores rows
    with no category, and filling them in one HTTP call per row is a request that
    legitimately runs for minutes once a model is behind the port. What makes this
    faster is not the saved round trips -- those are milliseconds -- but that the
    rows are asked about concurrently. See `batch.py`.

    **Not a replacement for `POST /categorize`.** That one is called while somebody
    is typing (#67) and while a row is being categorised on its own; a caller with
    one row should not have to wrap it in a list and unwrap the answer. The two
    share every rule that matters because `BatchItem` *is* a `CategorizeRequest`
    and `BatchAnswer` *is* a `CategorizeResponse`, so there is no second contract to
    keep in step.

    `def` and not `async def`, for the same reason as `categorize` above and with
    more riding on it: this handler blocks for as long as the slowest row takes, so
    declaring it `async` would hold the event loop for seconds and stall every other
    request in the process, health checks included.

    The log line is the durable record, the way #64's is on the .NET side and
    #65's is for the cache: this process scales to zero, so anything counted in
    memory describes at most one replica's afternoon. It names how many rows were
    asked about and how many came back, because those two numbers differing is the
    only symptom a dropped row has.
    """
    started = time.perf_counter()
    answers = answer_all(predictor, request.items)
    elapsed_ms = (time.perf_counter() - started) * 1000

    logger.info(
        "batch items=%d answered=%d concurrency=%d elapsed_ms=%.0f",
        len(request.items),
        len(answers),
        min(CONCURRENCY, len(request.items)),
        elapsed_ms,
    )

    return CategorizeBatchResponse(answers=answers)
