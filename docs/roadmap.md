# Roadmap

LandMoney tracks personal spending. The application is real -- the owner is
meant to actually use it -- but the goal behind it is a move from .NET
development into AI engineering with devops skills.

Started 2026-08-05.

## Why this shape, and what went wrong before

The predecessor, `netshift`, is a working CLI that inspects legacy `.csproj`
files. It is paused, not deleted. It delivered Python, Docker and CI, and then
stalled, for reasons worth keeping:

- **No user.** Nobody asked for a `.csproj` inspector, so the goal never felt
  legible. LandMoney has a user -- the owner, weekly.
- **No deployment.** Three of the four things the owner wanted were Python,
  Docker and CI/CD. The first three were delivered; nothing was ever deployed
  anywhere, and the roadmap had no plan to.
- **The AI was three phases away.** Weeks went into the infrastructure half
  that a .NET developer with devops interest already understood, translated
  into different function names. The mistake was recorded in netshift's own
  `docs/why-this-project.md` days before it was repeated.

Two things from netshift were kept deliberately, because they were right:

1. **The domain must be one the owner can judge without looking anything up.**
   Their own spending qualifies. This is what makes evaluation possible at all,
   and it is why a general-purpose chatbot teaches nothing.
2. **Evals before the first model call.** The common way to fail at entering
   this field is to start from the prompt and improve it by feeling for six
   months.

## Slice 1 -- it runs locally

**Skill:** the .NET half is comfortable and is meant to go fast. React is the
part that is genuinely new, and the reason this slice is no longer "none new".

- [x] `Transaction`: date, amount (`decimal`), currency, description,
      category -- #1, merged 2026-08-11. `Guid` key chosen knowingly with the
      index cost written down next to it. The date was a `DateTimeOffset` --
      `DateTimeOffset` over `DateTime` so there is no `Kind` to get wrong --
      and became a `DateOnly` in #17; `CreatedAt` still carries the offset
- [x] Postgres via EF Core, schema created by a migration rather than by hand
      -- #2, 2026-08-18. `numeric(18,2)` and `timestamptz` read back out of
      the running database, not only out of the migration file
- [x] `docker compose up` brings the database to healthy
- [x] A Web API that creates and lists transactions -- #3, 2026-08-19.
      Minimal APIs over controllers, since the automatic 400 that used to
      be the reason to prefer a controller is fifteen lines of endpoint
      filter here. .NET 10 ships that filter as `AddValidation()` and it
      was the first choice until the build refused it: the whole
      `Microsoft.Extensions.Validation` surface is `[Experimental]`
      (ASP0029) and needs a suppression to compile
- [x] A React client in TypeScript, built by Vite -- the scaffold and the dev
      proxy are #4, the form and the list are #6, merged 2026-08-23. The
      day-boundary question that blocked this was settled on 2026-08-18 in #17
      -- `OccurredAt` is a plain date, so grouping by day needs no timezone.
      The client validates the *shape* of a value and leaves the *bounds* to
      the server, so no limit is written down in two languages; the one thing
      #6 found that reading the diff would not have is that the dev proxy
      answers a refused connection with its own 502, so "the API is not
      running" never reaches the browser as a failed `fetch`
- [x] The client served by the .NET app as static files, one image -- #20,
      2026-08-23. The MVC leftovers went first, pulled forward earlier the same
      day because F5 in Visual Studio opened the Razor template's "Welcome" page
      on the API port, and being shown a stranger's landing page is a good way
      to believe the wrong application is running. What #20 then did: Vite's
      `build.outDir` points at `src/LandMoney.Web/wwwroot`, so there is no
      `dist/` and no copy step that CI performs and a developer does not;
      `UseStaticFiles` serves it with `immutable` on the hashed assets and
      `no-cache` on `index.html`; and `MapFallbackToFile` hands a client route
      the index page. `wwwroot` is git-ignored now that it is build output, and
      the Development-only redirect to the Vite dev server is gone -- `/` is the
      client on every environment.

      Three things reading the diff would not have shown, all found by sending
      requests. `MapFallbackToFile` matches `{*path:nonfile}`, and `nonfile`
      means "no extension", not "not the API" -- so `/api/nope` answered **200
      with `index.html`** until an explicit `/api/{**path}` catch-all was put in
      front of it. The fallback builds its own `StaticFileMiddleware` rather
      than reusing the registered one, so `/` and `/index.html` were two
      different answers for one file, and the one with no `Cache-Control` at all
      was `/`. And `MapStaticAssets`, the .NET 10 default that stood here,
      resolves everything through a manifest written when the .NET project
      compiles: a file appearing in `wwwroot` after `dotnet publish` is a silent
      404, which is what a wrong build order would produce. Its own warning
      names the alternative, and that alternative is what is in place now. The
      price is written beside the line -- `MapStaticAssets` emits `.br` at
      publish time, and on this bundle that was 196,604 bytes down to 52,814

**Done when:** a transaction typed into the form survives a restart of both the
app and the container.

**The UI changed on 2026-08-07**, two days in, from Razor Pages to React. The
reasoning is in `CLAUDE.md`; the short version is that the earlier choice
optimised for reaching the screen quickly, and the new one optimises for what
the project is for. The cost was named before it was accepted: roughly a week,
and a second language ecosystem to keep alive.

Kubernetes was raised at the same time and deliberately left out. Not because
it is not worth knowing, but because for two containers it produces weeks of
manifests and nothing visible, and it can be learned for free on a local
cluster whenever it is actually wanted.

## Slice 2 -- CI

**Skill:** the same discipline as netshift's CI, now with a compiled language
and a container in the loop.

- [x] A test project, and the first tests worth having -- #21, 2026-08-24.
      This was missing from the plan: slice 2 asked CI to run "build, test"
      while nothing in the repository was testable. The rules from #3 came
      first, because each of them fails silently when broken. 49 tests in
      `tests/LandMoney.Web.Tests`, xUnit, no `Microsoft.AspNetCore.Mvc.Testing`
      -- an `IEndpointFilter` is an object with one method, and
      `EndpointFilterInvocationContext.Create` builds its argument, so
      `WebApplicationFactory` bought nothing that was needed yet

      **`PlausibleDateAttribute` now takes its clock from a `TimeProvider`**,
      reached through `validationContext.GetService`. The alternative -- test
      relative to `DateTime.UtcNow`, change no production code -- lost because a
      test that computes today the way the attribute does asserts only that two
      copies of one expression agree, and it cannot ask what happens on a named
      day. Two of the tests live on named days: one pins a clock at 23:00 UTC
      whose local zone is UTC+14, so reading the local time would call it
      tomorrow, and one sits on 29 February to record that `DateOnly.AddYears`
      clamps the five-year bound to the 28th

      **The tests were checked by breaking the code.** 24 mutations, one rule at
      a time; every one was caught, and 48 of the 49 tests failed under at least
      one. The first attempt at the sweep reverted each mutation with
      `git checkout --`, which threw away the uncommitted production changes and
      quietly ran the next twelve mutations against the old code -- reverting from
      a file copy is what the script does now. The one test no mutation reaches
      asserts that `decimal` keeps its trailing zeros, which is a fact about .NET
      rather than about this repository, and is there to keep the test beside it
      honest

      Found in the review of #31, and the one that would have shipped:
      `PlausibleDateAttribute` built its failure messages with an interpolated
      `{latest:yyyy-MM-dd}`, which formats with the ambient culture -- and for a
      date that does not merely choose separators, it chooses the **calendar**.
      With `CurrentCulture` set to `ar-SA` the same format string answered
      "cannot be later than **1448-01-01**", a Hijri year, no exception and
      nothing in a log, under a date input reading `2026-06-16`. Nothing sets a
      culture in this application, so it was latent rather than live. Both
      messages now format with `InvariantCulture`, and a `[Theory]` over `ar-SA`
      and `de-DE` holds them there

      Two more found by running rather than reading. `Validator` tests
      `[Required]` on a property first and returns at once if it fails, so the
      other rules on that property never run -- an empty description reports
      "required" and never "too short", which means the count of messages under a
      key is not the count of broken rules. And the test project resolved EF Core
      10.0.4 while `LandMoney.Web` was compiled against 10.0.10, because
      `Microsoft.EntityFrameworkCore.Design` is what raises that version and
      `PrivateAssets="all"` correctly stops it flowing: 66 MSB3277 warnings, and
      a `FileLoadException` waiting for the first test that touches EF
- [x] GitHub Actions: build, test, on every push -- #22, 2026-08-24.
      `.github/workflows/ci.yml`, one job on `ubuntu-latest`, 22 seconds:
      client, then solution. Triggers are `push` to `main` plus
      `pull_request`, not push to every branch -- the literal reading runs
      twice per commit on a branch with an open pull request

      **`global.json` is new**, and it is the second half of the argument
      `.nvmrc` already won: one file that both this machine and the runner
      read, rather than a version typed into `ci.yml` that drifts from the
      installed one with nothing reporting it. `rollForward: latestFeature`,
      loose inside the major the way `.nvmrc` says `24` and not `24.19.0`

      **Checked by breaking it**, which is what #22 asked for and the same
      discipline as #21's mutation sweep. An off-by-one in
      `DecimalScaleAttribute` -- `MaxScale + 1`, a plausible loosening rather
      than an inverted comparison -- turned the check red: `Build` green,
      `Test` failing 9 of 51, exit code 1. Reverted in the next commit, so the
      evidence stays in the history instead of in a chat log

      Two things the first run measured rather than assumed. **`setup-dotnet`
      installs the exact version from `global.json` and does not apply
      `rollForward`**, so the runner is pinned outright and `rollForward` is a
      rule for this machine and for #23. And **`dotnet tool restore`, the step
      that compiles nothing**, was the only one to report anything worth
      knowing: `dotnet-ef` is pinned to 10.0.10 while the runner's runtime is
      already 10.0.11

      The action majors are worth not guessing: `checkout@v7`,
      `setup-node@v7`, `setup-dotnet@v6`. From memory all three would have
      been v5

      Follows on, and **done the same day**: `CLAUDE.md` said there was
      deliberately no ruleset on `main` because there was no CI to require.
      The owner turned one on once #32 was merged, requiring **`build`** -- the
      job, not `CI`, the workflow -- with an empty bypass list, so it applies
      to the owner too. What that ruleset says, and the trap it creates for
      `paths:` filters, is recorded in `CLAUDE.md`
- [x] Dockerfile for the web app, multi-stage, non-root user -- #23,
      2026-08-24. `node:24-slim` -> `sdk:10.0` -> `aspnet:10.0`, **350 MB**, of
      which 7.75 MB is this application and the rest is the base image. Runs as
      uid 1654 (`whoami` answers `app`). Verified against the compose Postgres
      rather than reasoned about: `/` is 200 `text/html`, `/api/transactions` is
      200 with 3,185 bytes of real rows, `/assets/index-BnxjKvxq.js` is 200 at
      196,604 bytes, `/api/nope` is 404 `application/problem+json`, and an
      invalid POST is 400 `application/problem+json` -- so the endpoint filter
      and `AddProblemDetails` survive the trip into a container

      **The insurance above was taken and it passed** -- `/` was requested, not
      assumed. What it did not catch is the same failure entering from the
      third door: `wwwroot` is git-ignored, so it is invisible in a diff *and*
      present on this machine, and without a `.dockerignore` line it would have
      been copied in from the local build. `COPY` merges directories rather
      than mirroring them, so stale hashed assets would ship forever and an
      image built with the node stage broken would still serve a working
      client. That line is now in `.dockerignore` with the reason beside it

      **Found by running: the first image contained
      `src/LandMoney.Web/appsettings.Development.json`**, an untracked file git
      has been hiding since 2026-08-05, because a `.dockerignore` pattern with
      no slash matches the repository root only -- `filepath.Match`, where `*`
      does not cross a `/` -- while the `.gitignore` line that looks identical
      matches at any depth. That copy held nothing but log levels. Every secret
      pattern carries `**/` now

      **`UseHttpsRedirection` in the container was measured**, which #23 asked
      for. Environment is Production, the branch runs, the middleware finds no
      port and logs `Failed to determine the https port for redirect` once, then
      passes the request through -- no loop, but by degradation rather than by
      design. Slice 3 puts a TLS-terminating ingress in front of exactly this

      Two things in the image nobody chose, written down so they are recognised
      rather than investigated: `dotnet publish` emits `.br`/`.gz` beside every
      asset even though `UseStaticFiles` will not serve them (`.js.br` is a 404
      -- unknown MIME type), and the runtime image has no
      `libgssapi_krb5.so.2`, so Npgsql prints a loader error to stdout at the
      first connection. Harmless, unfilterable, and not written through `ILogger`
- [ ] Image pushed to `ghcr.io` -- #24

Azure Container Registry was the first plan and lost to `ghcr.io`: same job,
but ACR Basic costs around 5 USD a month while GitHub's registry is free for
public repositories. One fewer paid service, one fewer set of credentials.

**Done when:** a fresh clone builds and tests on a machine that is not this one.

## Slice 3 -- deploy

**Skill:** the gap netshift never closed. This is the "CD" the owner asked for.

- [ ] Azure Container Apps, deployed from GitHub Actions, pulling from
      `ghcr.io` -- by hand first in #35, automated in #38. The order is
      deliberate: a deployment written straight into a workflow fails inside a
      runner where nothing can be inspected. Before either, #34 decides where
      Postgres lives once this is deployed, which is what the connection string,
      the migration step and the monthly cost all hang off

**Why not "all of it in GitHub".** GitHub covers two of the three layers: the
pipeline (Actions) and the registry (`ghcr.io`). It does not cover the third.
Pages serves static files -- HTML, CSS, JS -- and this application needs a live
process and a database beside it. Something has to run the container, and that
is what Azure is here for. Worth knowing the split rather than discovering it
halfway through a deployment.

- [ ] Real configuration and secrets handling -- no connection string in git
      -- #36
- [ ] Migrations applied as a deployment step, not on application startup
      -- #37, which also has to say out loud why `Database.Migrate()` on startup
      stays out: with `--min-replicas 0` a cold start would run migrations, and
      several replicas would run them at once
- [ ] The URL works from a phone. **Check `AbortSignal.any` on that phone
      first**, raised in review of #28: `api/transactions.ts` composes the
      request timeout with the caller's signal through it, and of everything the
      client uses it has the shortest history in shipping browsers -- Safari
      picked it up in 17.4, several minor versions after `AbortSignal.timeout`.
      Vite lowers syntax and does not polyfill a missing global, so on a browser
      without it the first `fetch` throws before any request is made, and the
      screen blames the wrong thing. Ten seconds on the actual device decides
      whether it matters; if it does, the fallback is to keep
      `AbortSignal.timeout` for the timeout and abort from the caller's own
      listener. Not worth writing blind before the device is known

**Three ways to apply a migration at deploy time**, named now so the choice is
not made by default later. `dotnet ef database update` from CI is the obvious
one and the worst fit: it needs the SDK, the tools and network reach from the
runner to the database. `dotnet ef migrations script --idempotent` produces SQL
that can be read before it runs, which is what a DBA would ask for.
`dotnet ef migrations bundle` produces a self-contained executable that needs
no SDK where it runs -- the container-shaped answer, and the one expected to
win here.

**On cost:** Azure Database for PostgreSQL is not free. The cheap route while
learning is Postgres as a container next to the app. That is not how production
is run, and the difference should be understood rather than glossed over.

**Done when:** a push to `main` reaches the running site with no manual step.

## Slice 4 -- the AI part, in the right order

**Skill:** the one all of this was for.

1. [ ] **Evals first.** 30-50 hand-labelled transactions with the category they
       should get. Metric and baseline defined **before** the first model call
       -- #25, and it is deliberately open now rather than when slice 4 starts.
       It is the only item in the project that depends on no code at all, and
       the only one that cannot be caught up later: data does not accumulate
       retroactively. Starting it now also forces the app to be used weekly,
       which is the habit netshift never formed

       **Everything except the labels landed 2026-08-24** in #25: the eleven
       closed categories with their boundary rules, the metric, the rules
       baseline, the scorer and 26 tests, in `evals/` with the decisions in
       `docs/evals.md`. `evals/transactions.csv` is a header and no rows, and
       that is the half nobody else can do -- 30-50 rows from real spending,
       labelled by hand. The database held 16 development rows (`Coffee`,
       `Trailing`, `Test from the review of #21`) with **zero** categories, so
       there was nothing to convert and inventing rows would have produced a
       number about invented spending

       **The metric is macro-averaged recall**, not accuracy, and the reason is
       asserted rather than argued: `test_score.py` builds 20 rows where 40% are
       `groceries`, answers `groceries` to all of them, and pins accuracy at 40%
       against a macro recall of 1/11. What it does not capture is written down
       beside it -- every wrong answer costs the same, abstention is not
       distinguished from confident error, and at ~4 rows per category anything
       under about 3 points is noise. That last number is the one to remember
       the first time a prompt change "improves" the score

       **`travel` and `education` were considered and folded in**, on an
       argument that comes from the metric rather than from taste: macro recall
       weights a two-row category exactly as much as a fifteen-row one, so a
       category too small to measure does not become harmless, it becomes a coin
       flip the average takes seriously. Three rows is the floor, five the
       target, and the scorer names any category under it
2. [ ] **A rules baseline.** String matching on the description. Score it.
       This number is what everything later has to beat, and it is often
       embarrassingly hard to beat

       **The rules exist and are unscored** -- `evals/rules.py`, 109 ordered
       substrings, #25. Written **before a single row was labelled**, which is
       the strongest available answer to the trap the issue names: rules tuned
       against the rows they are scored on have been taught the answers. The
       first number this produces is the baseline, and editing a rule after
       seeing which rows it missed has to be said out loud beside the result

       Seven ordering collisions are real rather than illustrative -- `gas
       station` before `gas`, `notebook` before `book`, `headphones` before
       `phone`, `taxi` before `tax`, `car rental` before `rent`, `coffee beans`
       before `coffee`, `bus ticket` before `ticket` -- and a test holds them
       there, because sorting the list alphabetically is the easiest way to move
       the baseline's score without meaning to. Bare `bus` is not a rule at all:
       it matches "business", and no ordering fixes that

       **No match predicts `unknown`, which is not in the vocabulary and is
       therefore always a miss.** Falling back to the most common category lost
       because it makes the baseline a fact about the label distribution rather
       than about the rules; falling back to `other` lost because it scores
       `other` rows right for the wrong reason. That decision survived a
       mutation sweep only after a test was added for it -- pointing
       `NO_PREDICTION` at `other` was the one mutation of six that the first 25
       tests did not catch

       **Known before the first run:** the substrings are English, because
       everything in this repository is. Descriptions typed in Russian or
       Romanian score close to zero, and that is a finding to record rather than
       patch around -- it is the strongest argument the model half will get
3. [ ] A Python service (FastAPI) that categorises a transaction. Called by the
       .NET app over HTTP, with a timeout and a fallback to the rules -- #39,
       which carries the rules from step 2 inside it so the baseline and the
       service stay the same code and the score keeps meaning something
4. [ ] An Anthropic adapter behind a port, plus a fake with canned responses so
       tests never hit the network and never cost money
5. [ ] Run the evals. Did the model beat the baseline? Record the number

**Done when:** the improvement over the baseline can be quoted as a number, and
the thing that produced the number can be shown.

## Slice 5 -- operations

- [ ] Redis: identical input must not be billed twice
- [ ] pgvector: find similar past transactions
- [ ] Token and cost accounting per request
- [ ] Graceful degradation: the AI service is down, the app still works
- [ ] Evals run in CI on every PR

## Deliberately not doing

Recorded so they do not creep back in:

- Authentication, roles, an admin panel -- until slice 4 is running
- Multi-currency conversion. Amounts keep their currency; no implicit maths
- Bank integrations. Manual entry and CSV import are enough to learn from
- Anything resembling investment or financial advice. Categorising past
  spending is a data problem; telling someone what to do with their money is
  not what this project is
