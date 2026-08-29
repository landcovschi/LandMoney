# categorizer

The rules baseline of #25, behind an HTTP endpoint -- #39.

**The decisions live in [`../../docs/evals.md`](../../docs/evals.md)** (the
eleven categories, the metric, why the baseline abstains) and in
[`../../CLAUDE.md`](../../CLAUDE.md) (the stack, and what lost). This file is
how to run it.

**There is no LLM call in here, and that is the rule rather than an omission.**
This service exists to be the thing a model has to beat. The seam it arrives
through is `Predictor` in `predictor.py`; the field that will tell the two apart
in stored data is `source`, and it is in the response today so that rows written
before the model exists can still say where they came from.

## Run it

```bash
uv sync
```

```bash
uv run uvicorn categorizer.main:app --reload --port 8000
```

```bash
uv run pytest
```

All three from this folder. `uv sync` is `dotnet restore`: it reads
`pyproject.toml`, resolves against `uv.lock`, and creates `.venv` -- and unlike
`dotnet restore` it also fetches the *interpreter* named in `.python-version`,
so a machine with no Python at all is one command away from running this.

`/docs` on the running service is a browsable form for the endpoint, generated
from the type annotations. It is the fastest way to try a description by hand.

## The endpoint

```
POST /categorize
{ "description": "Coffee and a croissant", "amount": 12.34, "currency": "EUR" }

200
{ "category": "eating-out", "source": "rules" }
```

`category` is `null` when the rules had no answer, which is a normal 200 and not
an error -- the baseline abstains on roughly a third of the labelled set. A body
that breaks the contract is a 422 from FastAPI before the handler runs.

`GET /health` answers `{"status":"ok"}` and is what `docker-compose.yml` gates
`depends_on` on.

## Why the rules live here and not in `evals/`

They were in `evals/` until #39 and moved without changing a character. The
scorer imports them from here now, so **the service and the baseline are the
same `predict`** -- a service scored through a copy of its own logic reports a
number about the copy.

`evals/score.py` reaches this package by putting `src/categorizer/src` on
`sys.path`, rather than by installing it. That keeps `python evals/score.py` a
command needing no `uv`, no virtual environment and no network, which is the
property that let #25 exist before any of this did.

The consequence to know before editing a rule: **it now moves the baseline and
the deployed answer at once.** `rules.py` already says what that costs -- a rule
edited after seeing which rows it missed produces a weaker kind of number, and
has to be said out loud beside the result.

## Layout

```
pyproject.toml        the .csproj
uv.lock               packages.lock.json -- committed, and what pins the versions
.python-version       the interpreter, the way .nvmrc names node
src/categorizer/
  categories.py       the eleven. Moved from evals/, unchanged
  rules.py            109 ordered substrings. Moved from evals/, unchanged
  contracts.py        request and response -- records with DataAnnotations
  predictor.py        Protocol (= interface) + the rules implementation
  prompt.py           what the model is told, its digest, and the answer schema
  anthropic_predictor.py   the model behind the same Protocol -- #59
  cache.py            the answer cache, on the model path only -- #65
  main.py             FastAPI: POST /categorize, GET /health
tests/
```

The doubled name in `src/categorizer/src/categorizer` is the src layout, and it
is deliberate: the import root is exactly one folder, so a test cannot pass
against source the built wheel does not contain. `pyproject.toml` has the
argument.


## Which predictor answers -- #59

`CATEGORIZER_PREDICTOR` picks it, and the default is the free one:

| value | what answers | costs money |
| --- | --- | --- |
| unset, blank, or `rules` | `RulesPredictor` -- 109 substrings | no |
| `model` | `AnthropicPredictor` -- one Claude call per request | **yes, per request** |

Anything else **stops the process** rather than falling back. That is deliberate
and it is the one place this service refuses to start: a typo that quietly served
the rules would let a deployment believe a model was running, and the score
recorded for it would be the baseline's under a different name. A container that
will not start says so in one line; a mislabelled number is found months later.

```bash
CATEGORIZER_PREDICTOR=model uv run uvicorn categorizer.main:app --reload
```

The key comes from `ANTHROPIC_API_KEY`, which the SDK reads itself -- it is never
named in an argument and never logged. Locally it belongs in `.env`, which is
git-ignored and which nothing here may print. **A missing key does not stop the
process**, measured rather than assumed: `anthropic.Anthropic()` constructs
without one and fails at the first request, so the service logs one error at
startup and then answers `category: null` for every row -- #39's fallback, with a
model behind the port. A *wrong* key behaves the same way, one 401 per request.

Three more knobs, all with defaults in `anthropic_predictor.py` and all there so
#60 can move them without editing code: `CATEGORIZER_MODEL` (`claude-opus-5`),
`CATEGORIZER_EFFORT` (`low`), `CATEGORIZER_TIMEOUT_SECONDS` (`6`).

The six seconds is chosen against the **.NET** side rather than this one:
`CategorizerClient` allows the whole call eight, so a request still running at six
has already lost its caller. `max_retries=0` for the same arithmetic -- the SDK's
default of two would let one call reach 18 seconds for an answer nobody is
waiting for.

### What the adapter may not do

Normalise the **input**. `Groceries` and ` groceries ` become `groceries` on the
way *out*; the description goes to the model exactly as it arrived. Tidying it up
here would improve this predictor and silently move the rules baseline it is
measured against -- the mutation #39 caught by hand, and there is a test for it.

A word outside the eleven is an abstention, not a twelfth category and not a
mapping: `food` stays `food` and is refused. The prompt's schema constrains the
answer to the eleven plus `unknown`, and the adapter checks anyway, because the
schema is a property of one route and the check is a property of the adapter.

## The answer cache -- #65

Identical input must not be billed twice. `CATEGORIZER_REDIS_URL` turns it on;
`docker-compose.yml` already points it at the `redis` service, so there is nothing
to set.

**Only the model path has one.** `cache_from_env` is called from
`AnthropicPredictor.from_env` and nowhere else, so with the rules answering, no
connection is opened and nothing here is read -- a network hop in front of a free
in-memory substring scan would make it slower and add a second thing that can be
down.

The key is a sha256 of four things, and each one is in it for a reason that has
already cost somebody something somewhere:

| part | what it prevents |
| --- | --- |
| the model id | a cheaper model serving the expensive one's answers under its own name |
| the effort | raising `CATEGORIZER_EFFORT` and measuring no change, because the old answers came back |
| `prompt.FINGERPRINT` | the first prompt edit serving yesterday's answers for ever |
| the exact text the model was shown | nothing -- it *is* the input, byte for byte |

**Nothing is normalised to build a key**, and that is the sharp edge of this
feature rather than a missed optimisation. `LINELLA` and `linella` are two keys,
because folding them would be a rule living in the cache path and nowhere else --
the same drift as the `.replace("-", " ")` mutation #39 caught by hand, which
looks like an improvement and quietly makes the recorded baseline a number about
code that no longer runs. The day folding is genuinely wanted it belongs in
`_user_message`, where the model sees it too and the eval number moves in the same
commit.

**Redis being down means "call the model", never "no category".** Every failure is
swallowed, counted and logged; the answer is unaffected.

What is stored is the answer and what the call cost -- tokens in, tokens out, and
the money, as billed at the time. **Nothing about the transaction:** the key is a
digest and the value is an answer, so a dump of this Redis says what was
categorised as what and never what was bought.

One line per lookup carries the running hit rate, because a cache nobody measured
is a cache nobody knows is working:

```
cache outcome=hit elapsed_ms=1 saved_now_usd=0.001234 stored_model=claude-opus-5 hits=7 misses=3 failures=0 hit_rate=70.0% saved_usd=0.008638
```

The totals are in-process and this container scales to zero, so they describe one
replica's afternoon -- the log line is the durable record and the last line a
replica writes is its whole story. A hit produces no `model_call` line, on purpose:
that line means a call was made, and counting them is counting the charges.

**A failed call and an unusable answer are never cached** -- neither is something
the model said, and storing one would freeze a network blip or a schema fault into
every future answer for that description. An abstention *is* cached: `unknown` is
an answer, it was paid for, and asking again buys the same word at the same price.

`CATEGORIZER_CACHE_TTL_SECONDS` defaults to thirty days. It bounds memory rather
than staleness -- the key already carries everything that would make an answer
stale.

**`evals/score.py` does not use the cache unless asked** (`--cache`). A scored run
is meant to be a measurement of the model, and a number produced by replaying calls
that happened days ago under a `.env` nobody remembers is not one -- two identical
runs would stop being evidence, because the second would be reading the first.
