"""Asking about many rows at once, and doing it in parallel -- #93.

Kept out of `main.py` for the reason `rules.py` is: the HTTP surface should be the
place a request is bound and a response is shaped, and this is neither. It is also
what makes the fan-out testable without a client -- a fake predictor that counts
threads is eight lines, and none of the tests here open a socket.

**What a batch buys, and it is not the round trip.** Twenty HTTP requests to a
service on the same network cost a few milliseconds more than one; twenty *model*
calls at about 2.1 s each (#60) cost forty-two seconds. Running them concurrently
is the whole of the gain, and one round trip is what makes it possible to run them
concurrently at all -- the caller cannot fan out on its own without deciding how
many connections it is allowed to open at the other service, which is a decision
that belongs on the side that knows what it is calling.
"""

import logging
import os
from concurrent.futures import ThreadPoolExecutor
from typing import Mapping

from categorizer.contracts import BatchAnswer, BatchItem
from categorizer.predictor import Predictor

logger = logging.getLogger(__name__)

# How many rows are in flight at once, unless configured otherwise.
#
# Eight, and the number is chosen against three things at once. It has to be well
# under the forty threads Starlette's worker pool gives a `def` handler, because
# this fan-out borrows from that pool and starving it would make the health check
# queue behind a batch. It has to be small enough that a burst of model calls is a
# burst and not a rate-limit incident -- eight concurrent calls is a fraction of
# what one workspace is allowed, and this service runs at one replica (#87). And it
# has to be large enough to matter: eight turns a twenty-row batch from forty-two
# seconds into about six.
#
# The bill is unaffected by this number. Concurrency changes when the calls happen,
# never how many there are; the count is the caller's, bounded by MAX_BATCH_ITEMS.
DEFAULT_CONCURRENCY = 8

# The ceiling on that, and it is a guard rather than a preference: the pool below
# is created per request, so a mistyped variable of 500 would be 500 threads for
# every batch. Sixteen is already past the point where the model, and not this
# process, is the thing being waited for.
MAX_CONCURRENCY = 16


def concurrency_from_env(env: Mapping[str, str]) -> int:
    """How many rows to have in flight, read once at import.

    Falls back rather than raising, which is deliberately the opposite of how
    `main.py` reads `CATEGORIZER_PREDICTOR` and the same as how
    `anthropic_predictor.py` reads its prices. The question is what each mistake
    costs. A typo there serves the rules while the deployment believes a model is
    running, which is a wrong number recorded under the right name; a typo here
    makes a batch slower, which is visible in the line this module logs and costs
    nobody an answer. Refusing to start over it would take a categorizer off the
    air to protect a thread count.
    """
    raw = env.get("CATEGORIZER_BATCH_CONCURRENCY", "").strip()

    if not raw:
        return DEFAULT_CONCURRENCY

    try:
        wanted = int(raw)
    except ValueError:
        logger.error(
            "CATEGORIZER_BATCH_CONCURRENCY is %r, which is not a whole number; "
            "using %d.",
            raw,
            DEFAULT_CONCURRENCY,
        )
        return DEFAULT_CONCURRENCY

    clamped = max(1, min(wanted, MAX_CONCURRENCY))

    if clamped != wanted:
        logger.error(
            "CATEGORIZER_BATCH_CONCURRENCY is %d, which is outside 1..%d; using %d.",
            wanted,
            MAX_CONCURRENCY,
            clamped,
        )

    return clamped


# Read once, for the reason `_PREDICTOR` in `main.py` is read once: the process is
# one container per configuration, and re-reading the environment per request would
# make two batches in one process able to disagree about it.
CONCURRENCY = concurrency_from_env(os.environ)


def answer_all(
    predictor: Predictor,
    items: list[BatchItem],
    concurrency: int = CONCURRENCY,
) -> list[BatchAnswer]:
    """One answer per row, in the order the rows arrived -- minus any that raised.

    **A row that raises is left out rather than allowed to fail the batch.** The
    single-row endpoint can answer 500 and lose nothing but the one question that
    was asked; here that would throw away ninety-nine answers that were already
    paid for. So the exception is logged with its traceback -- the only thing that
    distinguishes a bug in this process from the model being unavailable -- and the
    caller sees an id it sent and did not get back.

    Note that this is rare by construction rather than by hope: `AnthropicPredictor`
    already catches everything and answers `category: null`, so a raise here means
    a defect in this service rather than a bad day at the other end of the network.

    The concurrency is a parameter with a default rather than a module lookup, so a
    test names the number it is testing instead of setting an environment variable
    and hoping about import order -- the same reason `build_predictor` takes the
    environment as an argument.
    """
    if not items:
        return []

    # One worker per row when there are fewer rows than the limit, which keeps a
    # one-row batch from creating eight threads to use one. `max_workers` may not
    # be zero, which the guard above already rules out.
    workers = min(concurrency, len(items))

    # A pool per request rather than one for the process. What that costs is thread
    # creation, which is microseconds against a call measured in seconds; what it
    # buys is that nothing is shared between requests, so a batch cannot be starved
    # by another batch's queue and there is nothing to shut down when the process
    # ends. A module-level executor becomes the right answer the day the pool is the
    # thing being waited for, which at eight threads it is not.
    with ThreadPoolExecutor(max_workers=workers, thread_name_prefix="categorize") as pool:
        # submit, not map. `map` re-raises the first exception when the results are
        # iterated and abandons the rest, which is exactly the "one row costs the
        # batch" behaviour this function exists to avoid. Futures are collected in
        # request order, so the order of the answers is the order of the items with
        # no sorting anywhere.
        futures = [(item, pool.submit(predictor.categorize, item)) for item in items]

        answers: list[BatchAnswer] = []

        for item, future in futures:
            try:
                answer = future.result()
            except Exception:
                # The id is not in the message. It is the caller's own key for a row
                # of somebody's spending, and #64's rule about what may be written
                # down does not stop being true because a traceback is convenient.
                logger.exception("A row of a batch raised; it is left out of the answers.")
                continue

            answers.append(
                BatchAnswer(id=item.id, category=answer.category, source=answer.source)
            )

    return answers
