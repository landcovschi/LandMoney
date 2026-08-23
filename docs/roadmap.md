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
- [ ] GitHub Actions: build, test, on every push -- #22
- [ ] Dockerfile for the web app, multi-stage, non-root user -- #23.
      **The node stage has to produce `wwwroot` before `dotnet publish` runs**,
      and nothing will say so if it does not: `wwwroot` is build output and no
      longer exists in a clone, so the publish succeeds, the image builds, the
      API answers, and only `/` is a 404. Raised in review of #30, where the
      same failure through the other door is why `UseStaticFiles` was picked
      over `MapStaticAssets`. Cheap insurance: after `docker build`, request `/`
      and assert a 200 rather than trusting that the stages ran in the order
      they are written in
- [ ] Image pushed to `ghcr.io` -- #24

Azure Container Registry was the first plan and lost to `ghcr.io`: same job,
but ACR Basic costs around 5 USD a month while GitHub's registry is free for
public repositories. One fewer paid service, one fewer set of credentials.

**Done when:** a fresh clone builds and tests on a machine that is not this one.

## Slice 3 -- deploy

**Skill:** the gap netshift never closed. This is the "CD" the owner asked for.

- [ ] Azure Container Apps, deployed from GitHub Actions, pulling from
      `ghcr.io`

**Why not "all of it in GitHub".** GitHub covers two of the three layers: the
pipeline (Actions) and the registry (`ghcr.io`). It does not cover the third.
Pages serves static files -- HTML, CSS, JS -- and this application needs a live
process and a database beside it. Something has to run the container, and that
is what Azure is here for. Worth knowing the split rather than discovering it
halfway through a deployment.

- [ ] Real configuration and secrets handling -- no connection string in git
- [ ] Migrations applied as a deployment step, not on application startup
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
2. [ ] **A rules baseline.** String matching on the description. Score it.
       This number is what everything later has to beat, and it is often
       embarrassingly hard to beat
3. [ ] A Python service (FastAPI) that categorises a transaction. Called by the
       .NET app over HTTP, with a timeout and a fallback to the rules
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
