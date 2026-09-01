# CLAUDE.md

Instructions for Claude Code in this repository. Read automatically when a
session starts. It belongs to the repository owner -- edit it whenever the mode
below stops fitting.

## Who this project is for

The owner is a .NET developer moving into AI engineering with devops skills.
This is a learning project: working code matters, but skill gained matters
more. When the two conflict, skill wins.

The previous attempt at this (`netshift`, a sibling folder) stalled because the
plan spent weeks on infrastructure a .NET developer already knew, in a domain
nobody had asked for, with the AI work three phases away. That failure is the
reason for the rules below. It is written down in `docs/roadmap.md`.

## Working mode

**Split by who is strong where.**

- **C# and ASP.NET:** the owner writes it. This is their language. Claude
  reviews, points at trade-offs, and does chores -- but does not write the
  application for them.
- **Python, Docker, CI/CD, Azure:** teaching mode. Explain the approach, sketch
  the signatures and extension points, then let the owner write the body.
  Roughly 20 lines of new logic at a time before it must be split.

Either way:

- Explain through C# and .NET parallels. `Protocol` vs `interface`, pytest
  fixtures vs `IClassFixture<T>`, `uv` vs `dotnet restore`. A good analogy
  saves weeks.
- When asked to "just fix it": show the full error first, name the cause,
  propose an option, and **wait for agreement**. A silently fixed red test
  teaches nothing.
- If a decision is non-obvious, name the alternative and why it lost.

What to do without asking and without commentary: chores. Formatting, renames,
usings and imports, typos, boilerplate, running linters and tests.

## Scope

- **Keep the .NET slice thin.** One entity, one form, one list, then deploy.
  No roles, no admin panel, no reporting until the AI work is running. The whole
  point of the rewrite was to stop polishing the part that was already
  comfortable.

  **Authentication was in that list and came out on 2026-08-26**, in #52, and
  the honest record is that the rule was overridden rather than satisfied. #52
  opens with "Blocked on purpose. Do not start this before the AI half runs" and
  says it is unblocked when slice 4 can quote a number; steps 4 and 5 of slice 4
  (#59, #60) were open at the time, so there was no number. The owner was shown
  that, asked for it anyway, and that is their call to make. Written down this
  way so that nobody later reads the merged pull request as evidence that the
  bar had been met -- and so that the *rest* of the list keeps its force, which
  is the thing an unrecorded exception quietly destroys.

  What it cost, measured against the fear the rule exists to prevent: 12 files,
  33 tests, one migration, and no model call any closer than it was. The
  netshift failure was weeks of comfortable infrastructure with the AI three
  phases away; this is a day, and the AI work is one issue away rather than
  three. That is the argument for it having been affordable, not an argument
  that the rule was wrong.
- Do exactly what was asked. Spotted an adjacent problem? Mention it, do not
  fix it in the same pass.
- A database gets added when it has a job, not to have seen it. Postgres now;
  Redis when there are model responses worth caching.
- No new dependency without discussing it.
- **No LLM call before evals exist.** Hand-labelled transactions and a
  rules-based baseline come first. Without them "it got better" is a feeling,
  not a fact. This is the single rule carried over from netshift unchanged.

  **`docs/evals.md` is where that lives**, written 2026-08-24 in #25: the
  eleven closed categories and the boundary rules that make labelling
  repeatable, the metric (macro-averaged recall) and the five things it does
  not capture, and why the rules baseline abstains rather than guessing. It is
  the third place decisions are written down, after this file and the roadmap,
  and it is the one to read before touching anything in `evals/`. The code is
  stdlib-only Python with no `uv` project -- that arrives with the categorizer
  in #39, not before it.
- Never touch `.env`, never print its contents, never put a key or a
  connection string into an example command.

  **One sanctioned exception, agreed 2026-08-28 in #60**, and it is narrow: the
  eval scorer may be run as `set -a; . ./.env; set +a; uv run ...`, which loads
  the file into the environment of **that one command**. The value is never
  echoed, copied or written anywhere else. This is what `docker compose` already
  does with the same file, and it is what #76's step 3 asked for by a worse
  route -- exporting into the shell, or a Windows user environment variable,
  which a running process cannot see because the environment is read at start,
  so it would have cost a session restart every time. It honours #76's other
  decision untouched: `score.py` still does not parse `.env`, because the
  parsing happens in the shell rather than in the script, and the folder stays
  stdlib-only. Verifying a key is present is done by printing its **length**,
  never its value.

## Technical conventions

- **All text in this repository is English** -- comments, docs, commit
  messages, UI strings. It is the working language of the ecosystem, and
  Windows PowerShell 5.1 reads `.ps1` files as ANSI unless they carry a UTF-8
  BOM, so non-ASCII in tooling breaks the parser in miserable ways.
  **This extends to everything on GitHub**: issue titles and bodies, pull
  request descriptions, review comments. They are not literally in the
  repository, which is why this had to be said out loud on 2026-08-23. The
  chat conversation is Russian and stays that way -- it is not published, and
  nobody has to read it later.
- .NET 10 (LTS). Python >= 3.12 with `uv` when the categorizer arrives.
- Money is `decimal`, never `double` or `float`. Amounts are stored with their
  currency; there is no implicit conversion anywhere.
- Dates and times are stored in UTC, converted only for display. A field that
  a human types a day into is a `date` instead, with no zone at all -- see
  `Transaction.OccurredAt` below.
- **A number or a date crossing a boundary is formatted and parsed with
  `InvariantCulture`.** Not a style rule -- it has now bitten twice from opposite
  directions. `[Range]` limits are strings parsed at runtime, so a machine set to
  Romanian reads `"0.01"` as `1`, which is what
  `ParseLimitsInInvariantCulture = true` is for; and `PlausibleDateAttribute`
  wrote its bound back out through an interpolated `{date:yyyy-MM-dd}`, which
  under `ar-SA` produced a **Hijri** year rather than a Gregorian one (#31). The
  same format string, a different calendar, silently. Boundaries here means
  anything a client, a database or a test will read: messages, limits, query
  values. Text meant for a human to read in their own language is the exception,
  and this application has none.

- The .NET app and the Python service talk over HTTP with an explicit
  contract. Every network client gets a timeout -- without one an outage
  becomes a hang, and a hang costs far more to debug than an error.

## The stack, and what was rejected

Decided 2026-08-05. Recorded here so it is not re-argued from scratch.

- **UI: React with TypeScript, built by Vite. The .NET side is a Web API.**
  Changed on 2026-08-07, having been Razor Pages for two days. Razor was the
  faster route to something on screen and it was the right call while the
  question was "how quickly does this run at all". The reason for changing is
  not technical but about what the project is for: React is the standard a
  frontend is expected to be written in, and splitting the API from the client
  now means the Python categorizer later plugs into a boundary that already
  exists instead of one invented for it. The cost is real and was accepted
  knowingly -- roughly a week, and a second language ecosystem.

  **TypeScript, not JavaScript.** For someone coming from C# this is the
  cheaper of the two: interfaces, generics and compile-time checking already
  mean what they are expected to mean. Plain JavaScript would save a day of
  setup and spend it back on runtime mistakes a compiler would have caught.

  **The client is served by the .NET app as static files**, built into
  `wwwroot`, one image, one deployment. A separate nginx container was the
  alternative and lost for now on moving parts: it adds CORS, a second image
  and a second thing to deploy for no benefit at this size. It becomes the
  right answer once the Python service arrives and there are several
  containers anyway.

  **Vite writes straight into `wwwroot`, decided 2026-08-23** (#20):
  `build.outDir` is `../LandMoney.Web/wwwroot`, there is no `dist/`, and no
  copy step anywhere. What lost is Vite's default plus a copy in the CI
  workflow -- it keeps the client self-contained and drops cleanly into the
  multi-stage Dockerfile of #23, and it is a step CI performs that local
  development does not, so `dotnet run` would serve whatever was copied last
  with nothing reporting its age. The price of the route taken: the client's
  config now knows the server project's folder layout, and it is what has to be
  undone the day the client gets its own nginx container. `emptyOutDir: true`
  is required rather than chosen -- `outDir` is outside Vite's root and Vite
  will not delete anything out there unasked -- which is also why `wwwroot` no
  longer holds a `.gitkeep`: it is build output, it is git-ignored, and a fresh
  clone has no such folder until the client is built.

  **`UseStaticFiles`, not `MapStaticAssets`, decided 2026-08-23** (#20).
  `MapStaticAssets` is the .NET 10 default and resolves every file through a
  manifest written when the .NET project compiles; everything under `wwwroot`
  is produced by Vite, at a different moment. A file missing from that manifest
  is a **404 in a published application, with nothing in the log**, so building
  the client after `dotnet publish` fails as a blank page rather than as an
  error. In a Debug build a development handler serves it anyway and prints a
  warning that names the alternative outright: "If the file was not added to
  the project during development, and is created at runtime, use the StaticFiles
  middleware to serve it instead."

  What that costs, measured rather than guessed: `MapStaticAssets` writes `.br`
  and `.gz` beside every asset at publish time and negotiates them per request,
  taking this client's bundle from 196,604 bytes to 52,814. `UseStaticFiles`
  serves the file as it finds it. The 143 KB matters for slice 3's "the URL
  works from a phone" and is recoverable without touching the line --
  `ResponseCompression`, or the nginx container above. Also rejected:
  `MapStaticAssets` with the cache headers overridden, because the obvious
  override emits **two** `Cache-Control` headers -- the handler writes its own
  while the response is going out -- and the working version needs an
  `OnStarting` callback whose reason for existing nobody will remember.

  **Two things about the fallback that only requests reveal**, both fixed in
  #20 and both worth not rediscovering. `MapFallbackToFile("index.html")`
  matches `{*path:nonfile}`, and `nonfile` asks whether the last segment looks
  like a filename -- not whether the request was meant for the API. `/api/nope`
  has no extension, so it matched, and a wrong API path came back as **200 with
  `index.html`**. An explicit `app.Map("/api/{**path}", ...)` returning
  `Results.Problem(statusCode: 404)` sits in front of it; routing scores a
  literal segment above a catch-all, so the real endpoints are untouched. And
  `MapFallbackToFile` constructs its own `StaticFileMiddleware` instead of
  reusing the registered one, so a cache policy set on `UseStaticFiles` reached
  `/index.html` and not `/`. The overload taking `StaticFileOptions` is what
  makes the two agree.

- **Kubernetes: considered on 2026-08-07 and deliberately not adopted.** For
  two containers it is weeks of manifests, ingress and secrets that produce
  nothing a user can see, and a managed cluster costs real money for node
  VMs. The skill is worth having, and when it is wanted the honest way to
  learn it is a local cluster (`kind`, or the one built into Docker Desktop),
  which speaks the same API for free. Container Apps stays.
- **Tests: xUnit, in `tests/LandMoney.Web.Tests`, decided 2026-08-24** (#21).
  The template's default, and the honest reason is that there was no argument to
  have: xUnit, NUnit and MSTest all do this job, the SDK ships a template for
  each, and the one whose `[Fact]`/`[Theory]` split is written most widely is
  worth more than a comparison nobody will reread.

  What was decided is what stayed out. **`Microsoft.AspNetCore.Mvc.Testing` and
  `WebApplicationFactory` were not added.** An `IEndpointFilter` is an ordinary
  object with one method, and `EndpointFilterInvocationContext.Create` builds its
  argument, so every rule #21 listed is reachable without starting a server. What
  that leaves untested is real: that the filter hangs on the POST and not on the
  group, and that a 400 leaving the process carries the body `AddProblemDetails`
  writes. Both were checked by hand against the running app instead, and the day
  they need checking automatically is the day the package earns its place -- #23
  already has a candidate in "after `docker build`, request `/` and assert a
  200". **`Microsoft.Extensions.TimeProvider.Testing` stayed out** for the
  smaller reason that a frozen clock is six lines.

  The tests need no Postgres, no Docker and no network: nothing in them touches
  `AppDbContext`. Worth knowing for #22, where it means the job is `dotnet test`
  and not `dotnet test` plus a service container.

  A `FrameworkReference` flows through a `ProjectReference`, so referencing the
  web project is all it takes to reach `DefaultHttpContext`, `TypedResults` and
  `ServiceCollection`. Nothing has to be declared for them.

  **One version pin that looks arbitrary and is not.** The test project carries
  `Microsoft.EntityFrameworkCore.Relational` and uses no EF at all.
  `Microsoft.EntityFrameworkCore.Design` is what raises the EF graph to 10.0.10
  in `LandMoney.Web` -- the Npgsql provider only asks for 10.0.4 -- and its
  `PrivateAssets="all"` correctly stops that flowing downstream, so the test
  project resolved 10.0.4 against an assembly compiled for 10.0.10: 66 MSB3277
  warnings, and a `FileLoadException` waiting for the first test that touches EF,
  since assembly binding rolls forward and never down. The real answer is
  `Directory.Packages.props`, and it is what to reach for when there is a third
  project; it was not worth a repository-wide change inside #21. The version has
  to be kept equal to the web project's, and the warnings come back and say so if
  it drifts, which is the reason for not suppressing them instead.

- **`TimeProvider` reached through `validationContext.GetService`, decided
  2026-08-24** (#21). `PlausibleDateAttribute` read `DateTime.UtcNow` inside
  itself, and its own comment named this as the thing to fix "the day this gains
  a test". A DataAnnotations attribute is constructed by the runtime out of the
  arguments in its brackets, so there is no constructor to inject into and a
  service locator is the only door there is. `ValidationFilter<T>` now builds its
  `ValidationContext` with `HttpContext.RequestServices`, and `Program.cs`
  registers `TimeProvider.System` -- **which the framework does not do by
  default**, measured rather than assumed. Behaviour is unchanged either way; what
  the registration buys is that production and the tests walk the same path
  instead of production always taking the fallback.

  What lost: testing relative to `DateTime.UtcNow`, which needs no production
  change at all. A test that computes today the way the attribute does asserts
  only that two copies of one expression agree, and it would keep passing if the
  attribute switched to `DateTime.Today` -- the exact mistake the comment on that
  line exists to prevent. It also cannot ask what happens on a named day, and two
  tests live on named days: a clock at 23:00 UTC whose local zone is UTC+14, where
  reading local time lands on tomorrow, and 29 February, where `DateOnly.AddYears`
  clamps the five-year bound to the 28th.

- **A test suite is checked by breaking the code.** #21 asked for it in as many
  words -- "a test that cannot fail is decoration" -- and the answer was 24
  mutations, one rule at a time, each reverted before the next. Every mutation was
  caught; 48 of 49 tests failed under at least one. Three things that only doing
  it revealed. The first sweep reverted with `git checkout --` and threw away the
  uncommitted production changes, then ran twelve more mutations against the old
  code and reported nonsense -- **revert from a file copy, or commit first**. A
  substitution without `/g` hits the first match in the file, which for
  `validateAllProperties: true` was the *comment* above the call, so the mutation
  that mattered most silently changed nothing. And a rule guarded twice cannot be
  killed by a one-line mutation: the null check in `PlausibleDateAttribute` is
  redundant with the type check below it, which is fine, and means the mutation
  has to make the rule fail rather than merely removing a line.

- **Build and deploy: GitHub Actions.** Public repository, so the minutes are
  free.

  **`.github/workflows/ci.yml` is one job, decided 2026-08-24** (#22): client
  first (`setup-node` reading `.nvmrc`, `npm ci`, `npm run lint`,
  `npm run build`), then the solution (`setup-dotnet` reading `global.json`,
  `dotnet tool restore`, restore, build, test). `timeout-minutes: 10`, because
  the default is six hours and an indefinite hang blocks a required check
  rather than reporting anything. The lint step is there because
  `npm run build` is `tsc -b && vite build`, which type-checks and bundles and
  has no opinion about lint rules -- without it `.oxlintrc.json` would stay
  enforced exactly once, by hand, in #4. Two parallel jobs lost on moving parts -- two
  checkouts, two toolchain setups, two marks in one checklist, and later an
  artifact hand-off of `wwwroot` that does not exist today. The single job is
  already written in the order #23 requires, where `dotnet publish` does read
  `wwwroot` and nothing reports it if the client has not run first.

  **Triggers are `push` to `main` plus `pull_request`.** The literal reading of
  #22 -- push to every branch, plus pull request -- runs twice per commit on any
  branch with an open pull request. `concurrency` with `cancel-in-progress`
  drops a run a newer push has already superseded.

  What that costs, and it is the half that is otherwise discovered the hard
  way: **a branch with no open pull request gets no CI at all.** Correct here,
  since nobody commits to `main` and every change arrives through a pull
  request, so the only unchecked work is unfinished work -- but the first
  feedback arrives when the pull request is opened rather than when the branch
  is pushed, and waiting for a green tick before opening one waits forever.

  Adding `push` on every branch back would **not** be deduplicated by
  `concurrency`: a push run and a pull_request run for the same commit carry
  different `github.ref` -- `refs/heads/<branch>` against `refs/pull/<n>/merge`,
  which is the ref `actions/checkout` is seen resolving in the log. Different
  groups, so neither cancels the other. Two runs per commit is unavoidable with
  both triggers on, which is why one of them had to go rather than be
  deduplicated.

  Three arguments that are load-bearing and invisible where they are used.
  **Build and test both take `LandMoney.slnx`, not the web `.csproj`**, or the
  test project is never built. **Both say `-c Release`, and have to agree**, or
  `--no-build` looks in `bin/Debug`. Either mistake fails in the *test* step
  with a message about the project not being built, which reads like a broken
  test project rather than a wrong argument one line above. And
  **`working-directory` applies to `run` steps only** -- it does not reach an
  action's inputs, which is why `node-version-file` is a path from the
  repository root while `npm ci` runs from the client folder.

  **`dotnet tool restore` is kept although nothing here runs `dotnet ef`.** It
  checks that the pinned tool restores on a machine that is not this one, which
  is what slice 2 is for -- and it earned that on the first run, being the only
  step to report that `dotnet-ef` 10.0.10 is now behind the runner's 10.0.11
  runtime.

  **Caching `~/.nuget/packages` and `~/.npm` stays out** until there is a reason
  beyond tidiness; the whole run is 22 seconds. For the day it is wanted:
  `setup-dotnet`'s `cache: true` requires a `packages.lock.json`, which this
  repository does not have, so it is not a flag to flip.

  **The Python tree joined `build` on 2026-08-28** (#58): `setup-uv`,
  `uv sync --locked`, the categorizer's pytest suite, the scorer's own tests,
  and `python evals/score.py --check`. Until then nothing on a pull request
  touched `src/categorizer/` or `evals/` at all, so `build` was green over a
  service that does not import and a baseline that had silently moved -- which
  #39 said out loud and left.

  **Inside `build`, not a job of its own, and that is the whole of what makes it
  protect anything.** `build` is the required check the ruleset names. A second
  job is not required until the ruleset is edited, and -- measured on #42 -- a
  job skipped by `if:` reports `skipped`, which GitHub counts as satisfying a
  required check. The same paragraph's other half still holds: never a `paths:`
  filter, or a documentation-only pull request starts no workflow and blocks for
  ever at "Expected -- Waiting for status to be reported".

  **No Python version appears in the workflow.** `setup-uv` takes a
  `working-directory` of `src/categorizer`, and uv reads `.python-version` there
  and fetches that interpreter itself -- the one thing uv does that `setup-node`
  and `setup-dotnet` expect to have been done for them. Note that
  `working-directory` is an *input of this action* rather than the step key of
  the same name; `setup-node` has no such input, which is why `node-version-file`
  beside it is still a path from the repository root.

  **`setup-uv` is pinned to `v10.0.1`, and it is the one action here that cannot
  float on its major.** The version was read off the releases API on the day
  rather than written from memory -- the third time that rule has paid, after #22
  and #24 -- and then `@v10` failed the run before a single step executed:
  `Unable to resolve action astral-sh/setup-uv@v10, unable to find version v10`.
  **This action publishes no moving major tag.** It did up to v7 -- `v7`, `v7.6`
  and `v7.5` are all there -- and stopped; v8, v9 and v10 exist only as full
  versions. So #22's and #24's lesson needs a second half: check the current
  major, *and* check that the major is a tag at all. What the pin costs is that a
  patch no longer arrives by itself, which every other action in `ci.yml` gets
  for free.

  **`evals/` runs on the runner's own python, deliberately not through uv.** It
  is stdlib-only by decision, and CI is the only place that property is ever
  checked; running it inside the categorizer's virtual environment would let an
  accidental `import fastapi` in there pass. Neither eval step may take a
  `working-directory`: `score.py` finds the categorizer package by a path
  relative to its own file, and the failure of getting that wrong is an
  ImportError that reads like a missing dependency.

  **`--check` is the point of the issue, and it is new behaviour in `score.py`
  rather than a step in the workflow.** The scorer exits 0 when it produced a
  number, so a step that only runs it stays green while the number drifts.
  `--check` compares the run against **`evals/baseline.json`** -- the one place
  the number is asserted -- and exits **2**, which is deliberately not the 1 that
  means the scorer could not score: "the baseline moved" and "the check is
  broken" want different reactions from whoever reads the red step. Both
  percentages are compared **as `render` prints them**, to one decimal place,
  because that is how they are written down; one row of 53 is 1.9 points, so
  nothing a rule can do hides inside the rounding. The row count is compared
  too, so a CSV that gained rows reports as a changed eval set rather than as a
  broken rule -- the eval CSVs are data, and moving the number on purpose is a
  one-file edit made in the same commit.

  **Nothing in `test_score.py` asserts today's real number**, and that is a
  choice rather than an omission: its nine new tests cover the comparison against
  hand-built reports, so a rule reordered by mistake turns CI red on the number,
  in a step whose message names the file to update, instead of red on a test that
  somebody then edits until it is green. Checked by mutation the way #21 checked
  its suite -- seven mutations of `check`, each reverted from a copy of the file,
  all seven caught.

  **The number today is 56.1% macro recall and 56.6% accuracy over 53 rows.**
  The 60.8% / 62.2% recorded for #39 above is the **45-row** set of that day and
  is history rather than the figure CI asserts; #58's own text quotes it, and it
  had already been superseded by the rewritten rows behind #47.

  **No Python linter runs, because `pyproject.toml` configures none.** #58 asked
  for "the linter, whatever `pyproject.toml` already configures", and the honest
  answer is that there is nothing to run: adding ruff is a new dependency, and
  new dependencies are discussed rather than slipped in beside a CI change.

  **A third job, `deploy`, decided 2026-08-26** (#38), and it is a transcription
  of steps 12 and 13 of `docs/deploy-azure.md` rather than anything new:
  download the `efbundle` artifact from this same run, `azure/login`, read the
  connection string out of the Container Apps secret, apply migrations, then
  `az containerapp update --image ...:sha-<github.sha>` and check what is
  actually running. Migrate first, deploy second -- #37's order.

  **Authentication is OIDC, so there is no credential in the repository at
  all.** An app registration with no client secret, plus a federated credential
  in Entra ID saying that a GitHub Actions token for
  `repo:landcovschi/LandMoney:ref:refs/heads/main` may act as it; `azure/login`
  trades this run's token for an Azure one that dies with the job. What lost is
  `az ad sp create-for-rbac --sdk-auth` into `secrets.AZURE_CREDENTIALS`, which
  every tutorial shows and which is a long-lived password kept in a store built
  to hand it to any workflow in the repository. **The three ids are `vars`, not
  `secrets`** -- they name an app registration, they are not a credential, and
  filing them as secrets would imply there is something to leak. Creating the
  registration is the owner's act, and step 14 has the commands.

  **The subject is not the one every guide prints, and it cost the first
  deployment.** GitHub sends an *immutable* subject by default --
  `repo:<owner>@<ownerId>/<repo>@<repoId>:ref:refs/heads/main` -- and a
  credential written in the documented `repo:owner/name:...` form matches
  nothing. The error is a good one and prints the string it wanted, so the fix is
  a copy: `AADSTS700213: No matching federated identity record found for
  presented assertion subject '...'`. What confirms it is a default rather than
  something switched on is
  `gh api repos/<owner>/<repo>/actions/oidc/customization/sub`, which answers
  `use_default: true`, `use_immutable_subject: false` **and** an immutable
  `sub_claim_prefix` -- so there is no flag to have set wrongly. Read the prefix
  from that API rather than typing it; the numeric ids are the point, since they
  survive a rename of the account or the repository where the names do not.

  **The repository name is still case-sensitive in the half the image name is
  not.** GitHub puts `LandMoney` in the token; `metadata-action` lower-cases the
  same string for `ghcr.io`. The two systems disagree about the repository's
  name on purpose. Adding `environment:` to the job changes the subject again, to
  `...:environment:<name>`, and needs its own credential -- which is why the job
  has no environment: one fewer string that must agree with a string in another
  system.

  **The `concurrency` block had to change, and #38's own suggested fix does not
  work.** A run cancelled between "migrations applied" and "revision replaced"
  is worse than one that waits, and the issue proposes a job-level group on
  `deploy` without `cancel-in-progress`. Cancellation happens to the whole run
  and takes every job in it, so a job-level group changes nothing about that.
  What works is `cancel-in-progress: ${{ github.event_name == 'pull_request' }}`
  at the workflow level: pull requests keep the old behaviour, pushes to `main`
  queue instead -- a concurrency group without cancellation makes the second run
  wait rather than drop.

  **The deploy job has no `actions/checkout`**, which is #38's third trap
  answered rather than met. The bundle is an artifact of the same run that built
  the image, so the commit that produced the schema and the commit whose image is
  deployed are the same by construction; a checkout is a third copy that can
  differ from both. `download-artifact` is **v8 while `upload-artifact` is v7** --
  independently versioned, both current as of 2026-08-26, checked rather than
  remembered for the fourth time after #22, #24 and #37. They still agree,
  because v7 skips zipping only under `archive: false` and v8's new
  Content-Type check therefore unzips.

  **Measured on the first deployment, 2026-08-26.** A GitHub-hosted runner
  **does** reach the Postgres server through the 0.0.0.0 "all Azure services"
  rule -- `Apply migrations` connected and answered "No migrations were applied.
  The database is already up to date." in seven seconds. Hosted runners are Azure
  virtual machines and that rule admits Azure, so the reasoning was right; it is
  now a measurement rather than a likelihood, and step 14 keeps the temporary
  firewall rule written down for the day that rule is narrowed. The whole
  `deploy` job is **71 s**, of which 10 s is `az extension add` doing nothing but
  preparing, and 19 s is `containerapp update` -- against the 23.3 s cold start
  of #35, which is the difference between provisioning a revision and starting
  one from zero. The verification's retry **never fired**: the first `curl`
  answered 200. It stays anyway, because winning a race once is not evidence
  there is no race.

  **The failure that came first landed where the design put it**, which is the
  half worth keeping. The login failed on the subject above and the job stopped
  at step 2 of 6 -- before `containerapp secret show`, before the bundle, before
  any revision existed. `build` and `publish` were green, so the image for that
  commit was already on `ghcr.io` and the fix needed no new commit, only a
  re-run. Azure was never touched and the previous revision served throughout.
  That is the migrate-first order and the "log in before you do anything"
  ordering both paying for themselves on the first try.
- **`Dockerfile` at the repository root: three stages, non-root, decided
  2026-08-24** (#23). `node:24-slim` builds the client, `sdk:10.0` publishes the
  app, `aspnet:10.0` runs it as uid 1654. 350 MB, of which 7.75 MB is this
  application -- the rest is the base image, so there is nothing here worth
  optimising until the base is the thing being questioned.

  **`node:24-slim` over `alpine`**, checked rather than hoped: this client has
  three native dependencies (`@rolldown/binding-linux-x64`,
  `@oxlint/binding-linux-x64`, `lightningcss-linux-x64`) and
  `package-lock.json` carries the `-musl` variant of each, so alpine would work.
  slim wins on sharing a libc with the SDK stage, and the ~80 MB it costs never
  reaches the final image because the stage is discarded. **The `24` is written
  twice** -- here and in `.nvmrc`, which is what `ci.yml` reads -- and there is
  no clean fix: `FROM` cannot read a file, and an `ARG` declares the duplication
  without removing it. What makes it tolerable is that `.npmrc` sets
  `engine-strict` against `"node": ">=24.0.0 <25"`, so a wrong major fails at
  `npm ci` naming the version rather than building something subtly different.

  **The node stage's `WORKDIR` is not a free choice**, and this is #20's
  written-down price being charged for the first time. `vite.config.ts` writes to
  `../LandMoney.Web/wwwroot`, a path relative to the client folder, so the
  client's config knows the repository's layout. `WORKDIR /src/src/landmoney.client`
  reproduces that layout; a bare `/client` puts `wwwroot` at the image root,
  which works by accident.

  **`restore` and `publish` take the `.csproj`, not `LandMoney.slnx`** -- the
  opposite of the call `ci.yml` makes one folder away, for the opposite reason.
  There, `dotnet test --no-build` needs the test project built. Here it would be
  restored, compiled and thrown away: the image is not where tests run.

  **`global.json` and `NuGet.config` are copied before restore**, and both fail
  quietly if forgotten. `sdk:10.0` resolves to **10.0.400** today -- read off the
  registry's tag list, where 10.0.400 is the highest -- so the pin costs nothing
  and there is nothing to roll; without the file the image would build on
  whatever band the tag carries, a different compiler from this machine's with
  nothing reporting it. Without `NuGet.config` restore falls back to the image's
  default nuget.org, **which works**, and that is the problem: three environments
  resolving from different places while all three stay green.

  **`.dockerignore` patterns are not `.gitignore` patterns**, and the difference
  cost a leak to find. A `.gitignore` pattern with no slash matches at any depth;
  a `.dockerignore` pattern is matched against the whole path with Go's
  `filepath.Match`, where `*` does not cross a `/`. So a bare
  `appsettings.Development.json` means *the root one* -- and the first image
  built here had `src/LandMoney.Web/appsettings.Development.json` inside it, an
  untracked file git has been hiding since 2026-08-05. That copy held nothing but
  log levels. Every secret pattern now carries `**/`, because the rule broke
  silently and in the direction where the file that eventually holds a
  connection string is the one nobody re-checks. The same file must exclude
  `src/LandMoney.Web/wwwroot`, which is git-ignored and therefore invisible in a
  diff: without it the SDK stage starts with the last local build, the node
  stage's `COPY --from` lands on top -- `COPY` merges directories, it does not
  mirror them -- and stale hashed assets ship forever, while an image built with
  the node stage broken still serves a working client and says nothing.

  **What `UseHttpsRedirection` does inside the container, measured** because #23
  asked and slice 3 is about to depend on it. `ASPNETCORE_ENVIRONMENT` is unset,
  so the environment is Production and the `!IsDevelopment()` branch runs. The
  middleware then finds no port to redirect to and logs, once, `warn:
  Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3] Failed to
  determine the https port for redirect.` -- and passes the request through.
  `GET /api/transactions` is a 200 with no `Location` header. So there is no
  redirect loop, **by degradation rather than by design**: the same no-op the
  http launch profile used to rely on. The day someone sets `ASPNETCORE_HTTPS_PORTS`
  or adds `ForwardedHeaders` middleware, the answer changes and nothing in the
  Dockerfile will mention it.

  **That day was 2026-08-25** (#36). `ForwardedHeaders` is now in `Program.cs`,
  so `Request.IsHttps` is true behind the ingress, the warning above is gone,
  and the no-op is by design. Still nothing in the Dockerfile mentions it,
  exactly as predicted -- the record is in the Container Apps bullet below.

  **Two things in the image nobody chose.** `dotnet publish` writes `.br` and
  `.gz` beside every asset regardless of which middleware will serve them, so
  `wwwroot` carries six files where three are read; `UseStaticFiles` will not
  serve them -- `/assets/index-BnxjKvxq.js.br` is a 404, since the extension has
  no known MIME type -- so they are dead weight rather than a surface, and about
  60 KB of it. And the runtime image lacks `libgssapi_krb5.so.2`, so Npgsql
  prints `Cannot load library libgssapi_krb5.so.2` to stdout at the first
  connection. It is harmless -- password authentication does not use GSSAPI and
  the queries run -- but it is written by the loader rather than through
  `ILogger`, so it carries no level and cannot be filtered, and in an aggregator
  it reads as a failed start. `apt-get install libgssapi-krb5-2` in the final
  stage silences it and puts apt into the image that ships; left alone
  deliberately, and written down so it is recognised rather than investigated.

  **Three more things `ls /app` raises, settled in review of #40.** The native
  apphost is gone -- `-p:UseAppHost=false`, because `ENTRYPOINT` names the dll
  and nothing ever launched the ~70 KB binary beside it; the publish layer went
  from 7.84 MB to 7.75 MB, which is not the point, `ls /app` being a shorter
  question is. **`LandMoney.Web.pdb` stays on purpose**, and this is the one
  worth writing down because it is what a later reader deletes on principle:
  without it a startup failure is a bare frame, and with it the image answers
  `at Program.<Main>$(String[] args) in /src/src/LandMoney.Web/Program.cs:line
  59` -- on a service whose logs are the only debugger it gets in slice 3, that
  is worth 27 KB. Note which path that frame carries: the build stage's, which
  does not exist in the running image. The line number travels, the file does
  not. And **`web.config` stays because there is no flag for it** -- the web SDK
  emits it on every publish, it means nothing outside IIS, and suppressing it
  takes an MSBuild property in the csproj, which would put a container's concern
  into the application's project file.

  **No `HEALTHCHECK`, and neither consumer would read one.** Container Apps
  (#35) ignores a Dockerfile healthcheck outright and probes over HTTP from
  outside, declared in the app spec. `docker compose` is the one that would read
  it, and #39 is when it matters -- the categorizer arrives beside this service
  and the app then wants the `depends_on: condition: service_healthy` treatment
  postgres already has. The obvious `HEALTHCHECK CMD curl -f
  http://localhost:8080/` will not work: measured, the runtime image has
  **none of curl, wget, nc or ping**. The aspnet image is deliberately that
  bare, so the choice when the day comes is an apt layer this image otherwise
  does not need, or letting compose probe from outside the container instead of
  inside it.

- **Image registry: `ghcr.io`.** Azure Container Registry does the same job
  and costs around 5 USD a month for Basic; GitHub's is free for public
  repositories.

  **Pushed by a second job in `ci.yml`, decided 2026-08-24** (#24).
  `needs: build` plus `if: github.event_name == 'push'` -- which means "pushes
  to main" only because the trigger is already narrowed to `[main]` at the top
  of the file, and which keeps meaning that after a branch rename where the
  literal string would not. Tags are `sha-<40 characters>` always and `latest`
  on the default branch; **`latest` is for running the thing by hand and is
  never what gets deployed**, because it cannot be rolled back to and a
  deployment that cannot name the commit it is running cannot be reasoned about.
  Slice 3 deploys the SHA tag or the digest.

  **`permissions` belongs on the job.** A job-level block *replaces* the
  workflow-level one rather than merging with it, so `contents: read` has to be
  restated beside `packages: write` or `actions/checkout` loses its token. That
  same replacing is the point: `packages: write` written at the workflow level
  would hand a token that can push images to the job that runs `npm ci` and
  `dotnet test`. The workflow-level default stays `contents: read`.

  **`docker/setup-buildx-action` is required, and forgetting it is silent.**
  `cache-to: type=gha` is a buildx cache exporter and the default `docker`
  driver has no exporters at all, so the build succeeds, the push succeeds, and
  the cache is never written -- the only symptom being that the next run is not
  any faster. `mode=max` rather than the default `min` for the mirror-image
  reason: `min` exports only the final stage's layers, and this Dockerfile's
  expensive ones (`npm ci`, `dotnet restore`) live in the two stages that get
  discarded.

  **Image names are lower-case and `${{ github.repository }}` is not.** Measured:
  `docker build -t ghcr.io/landcovschi/LandMoney:local-24 .` answers `ERROR:
  failed to build: invalid tag "...": repository name must be lowercase`. It
  survives as the `images:` input only because `metadata-action` sanitizes it --
  its README promises to lowercase the image name -- so nothing in that job may
  name the image except through the `meta` outputs. The trap is live again the
  moment anyone writes a `docker` command by hand there.

  **`labels:` is load-bearing, not cosmetic.** `metadata-action` derives
  `org.opencontainers.image.source` from `github.repository`, and `ghcr.io`
  reads that label to link the package to the repository. Without it the package
  exists at the account level with nothing pointing at it.

  **The package is private by default even though the repository is public**,
  and it has to be made public once, by hand, in the package settings. That is
  the owner's job, not Claude's, and skipping it means slice 3 needs a pull
  secret it should not need.

  **The action majors are worth checking every time.** `login-action@v4`,
  `metadata-action@v6`, `build-push-action@v7`, `setup-buildx-action@v4` as of
  2026-08-24 -- from memory they would have been v3, v5, v6 and v3, all a major
  behind. #22 recorded the same lesson for `checkout`/`setup-node`/
  `setup-dotnet`; this is the second time, so it is a rule rather than an
  anecdote.
- **Hosting: Azure Container Apps.** GitHub cannot host this -- Pages serves
  static files only, and this needs a live process plus a database. Container
  Apps was picked over App Service because a second container (the Python
  categorizer) is coming, and it scales to zero when idle.

  **It is deployed, by hand, since 2026-08-25** (#35), and every command is in
  **`docs/deploy-azure.md`** -- the fourth place decisions are written down,
  after this file, the roadmap and `docs/evals.md`, and the one to read before
  touching anything in Azure. What exists: resource group `rg-landmoney` in
  **`polandcentral`**, holding `psql-landmoney-pl`, the environment
  `cae-landmoney`, the app `landmoney`, and a Log Analytics workspace nobody
  asked for that `env create` makes on its own. The URL is
  `https://landmoney.redstone-8c11320c.polandcentral.azurecontainerapps.io`; the
  random middle label is assigned per environment and cannot be chosen.

  **The region is not the one that was picked.** West Europe was chosen and is
  `OfferRestricted` for Postgres on a new subscription -- so is Germany West
  Central. `az postgres flexible-server list-skus -l <region>` reports it in a
  `reason` field before `create` spends four minutes failing. Poland Central,
  North Europe, Sweden Central, France Central, Italy North, Norway East,
  Switzerland North, UK South and Spain Central were open on the day. That is a
  property of the subscription and the date rather than a fact about Azure, so
  it is re-checked rather than trusted -- and it has to be true for **Container
  Apps in the same region**, which is a separate query against
  `Microsoft.App`'s `managedEnvironments` locations.

  **`az` has moved under the documentation, four times in one session**, and
  each one fails as an argument error that reads like a broken command rather
  than a stale flag. `--high-availability` is now `--zonal-resiliency`;
  `--database-name` on `flexible-server create` is elastic-clusters-only, so the
  database is its own `db create`; `az provider register` takes **one**
  `--namespace` and silently drops the rest of them; and `az login` itself
  crashes on a fresh account inside its interactive tenant picker
  (`AttributeError: 'NoneType' object has no attribute 'get'`), fixed by
  `az config set core.login_experience_v2=off`. The rule this earns is the same
  one #22 and #24 already earned for GitHub Action majors: **read the CLI's own
  `--help` rather than a blog post**, and prefer a read-only probe to a create
  that fails slowly.

  **`--public-access None` disables public networking**, and does not mean
  "public, with no firewall rules yet". The next command then fails with
  `Firewall rule operations are not supported for a server without public access
  enabled` -- a message about firewalls, for a cause four commands earlier.
  `update --public-access Enabled` recovers it without recreating the server,
  because nothing had been locked into a VNet. Its dangerous neighbour is `All`,
  which opens the server to the whole internet, and omitting the flag is not
  neutral either: the default writes a rule for whatever IP the machine has.

  **The firewall is the 0.0.0.0 "all Azure services" rule**, which is the
  compromise #34 named and #35 had to take: a Consumption-profile Container App
  has no stable outbound IP, so there is no address to pin. It admits every
  Azure tenant's resources, guarded by the password and enforced TLS. VNet
  integration was the alternative and lost on a cost easy to overlook -- with
  private access this machine cannot reach the database at all, so the migration
  would need a jumpbox. **That is the answer to reopen alongside Neon when the
  free year ends**, since both questions are "is this still the right shape".

  **`SSL Mode=Require` needs no `Trust Server Certificate=true`,** measured from
  both this machine and inside the container. Npgsql 8 changed `Require` from
  "encrypt without checking" to "encrypt and validate", so it was a real
  question; Azure's chain to DigiCert Global Root G2 is trusted by Windows and
  by the Debian runtime image alike. Nothing in the connection string tells a
  client to skip verification, and that is the property to protect if it is ever
  edited. One spelling note that is not cosmetic: the secret uses `SslMode` and
  `CommandTimeout` without spaces, because Npgsql normalises keywords by
  stripping them and a space-free value survives PowerShell handing it to a
  `.cmd` shim.

  **`azure.extensions` is a dynamic parameter** -- `isConfigPendingRestart` is
  `false` after setting it -- so slice 5 needs no restart window for `vector`.
  It is already set.

  **Cold start from zero: 23.3 s, against 0.23 s warm**, measured 2026-08-25
  one minute after `replica list` first reported zero. That is the price of
  `--min-replicas 0` and it is larger than it sounds. **Scale-in took about
  fourteen minutes**, although the scale block says `cooldownPeriod: 300` --
  the cooldown is a floor, not a schedule, so anything measuring this has to
  wait for `replica list` to report zero rather than trust the clock.

  **The client's timeout is shorter than the cold start**, which is the part
  worth carrying forward. `src/landmoney.client/src/api/transactions.ts` sets
  `REQUEST_TIMEOUT_MS = 10_000`, and its comment -- "generous for a Postgres on
  the same machine" -- records exactly the assumption that stopped being true
  the day this deployed. Opening the URL cold is *fine*: the document request
  pays the 23 s, a browser imposes no timeout of its own on a page load, and the
  first `fetch` then meets a warm container. The failing case is a **tab left
  open** past the idle window, where the next `fetch` gives up at 10 s and a
  retry works because the first attempt started the container. Deliberately not
  fixed in #35. Before anyone raises the constant: that makes a genuine hang
  take longer to report, which is the failure the timeout exists to catch. The
  alternatives are a longer timeout on a session's first request only, a warm-up
  request at page load, or `--min-replicas 1`, which surrenders the reason
  Container Apps beat App Service.
  **Configuration reaches the deployed app by three roads and the code knows of
  none of them, settled 2026-08-25** (#36). `appsettings.json` holds log levels
  and is public; user-secrets holds the local connection string and is a
  development-machine feature that does not exist in a container; a **Container
  Apps secret** holds the deployed one, referenced as
  `ConnectionStrings__Default=secretref:pgconn` rather than pasted into an
  environment variable. `Program.cs` asks `GetConnectionString("Default")` and
  branches on nothing. The full picture, including how to rotate the secret, is
  step 12 of `docs/deploy-azure.md`; `README.md` carries the short version,
  because configuration is the one part of a deployment that never appears in a
  diff.

  **`ASPNETCORE_ENVIRONMENT=Production` is set although it changes nothing.**
  Measured in #35 before setting it: the container already logged `Hosting
  environment: Production`, since that is the framework default when the
  variable is absent and the `aspnet` image does not set it. The reason to
  write it down anyway is what hangs off it -- `UseExceptionHandler`,
  `UseHsts`, `UseHttpsRedirection` and `UseForwardedHeaders` are all gated on
  `!IsDevelopment()`, so a typo in that variable turns four things off at once
  and says nothing. **`--set-env-vars` adds, `--replace-env-vars` removes
  everything else**; one word apart in the same help text, and the wrong one
  deletes the connection string.

  **`UseForwardedHeaders`, `XForwardedProto` only, decided 2026-08-25** (#36),
  and the reason is not the redirect the issue asked about. TLS ends at the
  ingress, so inside the container `Request.IsHttps` is false, and
  `HstsMiddleware` returns early on exactly that -- **the deployed app was
  sending no `Strict-Transport-Security` header at all**, measured with `curl
  -I` before any of this. `UseHttpsRedirection` at least announced its
  uselessness once per start; HSTS was silent, which is the worse of the two.
  With the scheme forwarded, HSTS emits and the redirect becomes a no-op *by
  design* rather than by degradation -- a different thing to read in a log --
  and the line stays for the day something does forward plain http.

  **Confirmed deployed on revision `landmoney--0000002`:** the header is
  `max-age=2592000`, and the redirect warning is gone from the startup log after
  appearing at every start since #23 predicted it. That is also the only proof
  obtainable that **the ingress sends `X-Forwarded-Proto`** -- nothing in this
  application echoes a request header, so the emitted HSTS header is the
  measurement rather than a symptom of one.

  **`XForwardedFor` deliberately stays out.** It is what every example pairs
  with `XForwardedProto`, and taking it would set `RemoteIpAddress` from a
  header on a middleware whose trust list must be cleared. Nothing here logs or
  rate-limits by address, so it buys nothing today and is a spoofable client IP
  the day something does.

  **Clearing `KnownIPNetworks` and `KnownProxies` is required, and a local test
  cannot show it.** The defaults trust one proxy -- loopback -- and the ingress
  is another pod, so leaving them turns the header into a silent no-op. Checked
  by mutation the way #21 checked the suite, and the trap is the check itself:
  from `localhost` the mutated build behaves identically, because the request
  comes from the one address the defaults trust. It has to be sent to the
  machine's LAN address, and only then does the mutated build drop the header.
  **A proxy-trust bug cannot be reproduced from the machine running the
  process.** Two smaller ones found the same afternoon: `KnownNetworks` is
  `[Obsolete]` in favour of `KnownIPNetworks` (ASPDEPR005, the compiler doing
  by itself what #22 and #24 had to do by hand for action majors), and
  `HstsOptions.ExcludedHosts` contains `localhost`, `127.0.0.1` and `[::1]` by
  default -- so the first attempt at this measurement showed no header for a
  reason that had nothing to do with the change.

  What lost: deleting `UseHsts` and `UseHttpsRedirection` outright and recording
  that TLS enforcement lives at the ingress. That is true -- the ingress answers
  http with its own 301, measured, and that response carries no `server:
  Kestrel`, which is how it is known not to come from Kestrel. It lost on making
  the application depend on a property of one host: behind the nginx container
  expected later, or under plain `docker compose`, both protections would be
  gone with nothing reporting it.

  **The database's FQDN is `<server>.postgres.database.azure.com` in the
  runbook**, scrubbed in #36. The app's FQDN stays written out everywhere -- it
  is a public website. The database's is password authentication reachable from
  every Azure tenant through the 0.0.0.0 rule, in a public repository that also
  names the admin user, and there is no reason to publish two of the three
  things an attempt needs. It is a speed bump and not a control: the names table
  still carries the server name and the pattern is obvious. What it is worth is
  that grepping the repository for the deployed host name answering nothing
  stays a check that means something -- run with the name typed on the command
  line, never written into a file, or the documentation of the check becomes the
  only thing the check finds. Which is what happened on the first run of it.

- **Authentication: ASP.NET Core Identity, a username and a password, a form in
  the client -- decided 2026-08-27** (#52). Registration needs an invite code;
  there is no password reset and no email. The wiring is
  `src/LandMoney.Web/Auth/`, the commands are step 15 of `docs/deploy-azure.md`,
  and the screen is `src/landmoney.client/src/components/LoginForm.tsx`.

  **This replaces an OpenID Connect decision taken the previous day, in the same
  issue and the same branch**, and the reversal is the part worth keeping. #52
  lists three options and recommends the first or the second on the grounds that
  "the value here is a closed door, not a user-management system". Claude followed
  that recommendation and wired OpenID Connect against a configurable provider.
  The owner then asked for the third: a login form, a password, and a name in the
  header, which is what they had wanted from the start. Nothing about the
  recommendation was wrong; it answered a question about cost, and the owner was
  answering a different one about what the application should feel like. Recorded
  because the merged branch shows only the ending, and because it is the second
  time in two days that a written recommendation was taken as a decision when only
  the owner could make it.

  **What it cost to change direction, measured**: `AuthenticationSetup.cs` and
  `AuthEndpoints.cs` rewritten, one package swapped, one migration added, and the
  client's login screen written. Everything about *ownership* survived untouched --
  the `owner_id` column, the global query filter, the `SaveChanges` stamping, the
  index and its tests. That is the payoff of `ICurrentUser` being one property
  rather than a provider-shaped interface: the auth subsystem was swapped a day
  after it was written and the domain half did not notice. It is also the argument
  that kept `owner_id` a plain string rather than a foreign key to `asp_net_users`
  -- an FK would have made the transactions table depend on the schema of whichever
  subsystem happened to be signing people in, and that subsystem has now changed
  once already.

  **What lost, and why the earlier reasoning did not survive contact.** *Container
  Apps Easy Auth* still loses, for the reason #36 established: it makes
  authentication a property of one host, and under `docker compose` or behind the
  nginx container expected later there would be none, with nothing reporting it.
  *OpenID Connect against an external provider* is the one that was built and
  removed. It is genuinely cheaper to operate -- no password to store, no lockout
  to tune, nothing in the database worth stealing -- and it was rejected on product
  grounds rather than technical ones: it makes signing in a redirect to somebody
  else's page, and it makes "who may use this at all" a property of the provider's
  audience setting rather than of this application. The invite code below is what
  buys that decision back.

  **`AddIdentityCore`, not `AddIdentity`,** and the difference is why the
  registration is three calls instead of one. `AddIdentity` registers its own
  cookie schemes *and* points the default challenge at `/Account/Login` -- a Razor
  page this repository deleted in #20 and would not want back.
  `AddIdentityCore` registers the managers and no schemes, so
  `AddAuthentication(...).AddIdentityCookies()` beside it is the only cookie
  configuration there is. `.AddSignInManager()` has to be added explicitly or
  `SignInManager` cannot be resolved, and the failure is at the first request
  rather than at startup.

  **Three endpoints written by hand rather than `MapIdentityApi<IdentityUser>()`,**
  which is one line and was the first choice. It maps nine: four of them need an
  email sender this application deliberately does not have, so they would answer
  200 and send nothing -- an endpoint that reports success and does nothing is
  worse than one that is absent. Sixty lines of `UserManager` and `SignInManager`
  calls is the same machinery underneath with nothing in it that does not work.

  **`lockoutOnFailure: true` is the argument that matters on the whole page.** The
  lockout policy in `IdentityOptions` does nothing unless a call opts into it, so
  with `PasswordSignInAsync`'s default of `false` the configured five-attempt limit
  would sit in the options object looking like protection while an attacker guessed
  at whatever rate the network allowed. Measured rather than assumed: five wrong
  passwords, and the *correct* one is then refused with the lockout message.

  **A wrong password and a username that does not exist return the same
  sentence**, so the endpoint cannot be used to find out which usernames are real.
  Lockout deliberately breaks that symmetry -- it confirms the account exists --
  and is the accepted price of not leaving somebody staring at "wrong password"
  while typing the right one.

  **Password rules are length over composition**: ten characters, no required
  digit, case or symbol. Identity's defaults are the opposite (six characters with
  four character classes), and character-class rules push people towards
  `Password1!`, which is worth less than four more characters. The number is
  written twice, here and as a hint in `LoginForm.tsx`, and that is the two-places
  problem #6's validation rule exists to avoid -- taken knowingly, because a
  password rule discovered by failing is the worse trade.

  **No email address is collected at all.** There is no flow that would send one:
  password reset was left out in the same decision, because it means an external
  provider, an API key, a sender domain and deliverability to debug. Storing a
  personal email for a flow that does not exist is worse than not storing one. What
  it costs is written into step 15: a forgotten password is a command run against
  the database, and it is the owner who runs it.

  **Registration needs an invite code, from configuration.** The deployed URL is
  public, and open registration with no email confirmation is a sign-up page for
  anything that finds it. `RegistrationPolicy` is a record with the code and a
  `RequiresInvite` flag, and the two are deliberately separate: "needs a code, and
  none is configured" is the fail-closed state, and inferring it from the code
  being null would make it indistinguishable from "needs no code" -- which is the
  wrong direction to guess in. Comparison is `CryptographicOperations.FixedTimeEquals`,
  because a code is a shared secret and `==` leaks its matching prefix through
  timing.

  **The three-branch order from the OpenID Connect version was kept, and it is the
  same trick for the same reason.** Configured is tested first, so the deployed
  application takes that branch whatever `ASPNETCORE_ENVIRONMENT` claims to be; the
  Development branch, which lets registration proceed with no code, cannot be
  reached by mistyping one environment variable. #36 recorded that four middlewares
  hang off `!IsDevelopment()`; this is a fifth thing that must not be.

  **There is still no `?? throw`, and #57 is still the reason.** `efbundle` runs
  `Program.cs` from a directory holding nothing but itself, in Production, with no
  `appsettings.json`. A missing invite code closes registration and logs an error;
  it does not stop the process starting, and existing accounts still sign in.

  **Every refusal is a status and never a redirect.** Identity's cookie handler
  redirects to `/Account/Login` and `/Account/AccessDenied` by default; neither
  exists, so without `OnRedirectToLogin` and `OnRedirectToAccessDenied` overridden
  the client would receive 404 HTML where it expected JSON and report a parse error
  about a request that was actually refused.

  **`SameSite=Lax` is the CSRF protection, and there is no antiforgery token
  anywhere.** A Lax cookie is withheld from every cross-site request that is not a
  top-level GET navigation, so a form on another site cannot POST to
  `/api/transactions` with this user's session attached. The JSON `Content-Type` the
  client sends is a second lock on the same door -- a cross-site form cannot set it
  without a preflight this server never answers. Changing that one word to `None`
  removes both silently.

  **The shell is now anonymous, and that is a change of shape rather than a
  loosening.** Under OpenID Connect, `MapFallbackToFile` required authorization, so
  a signed-out visitor was redirected to a provider. With the form inside the
  client, the shell is exactly what a signed-out visitor needs -- protecting it
  would mean refusing the request whose job is to deliver the way back in. It also
  makes the pipeline honest about what was already true: `UseStaticFiles` is not an
  endpoint and consults no authorization metadata, so `/index.html` was public
  either way.

  **The seven Identity tables had to be renamed by hand, and finding that out cost
  a schema that was half one thing and half the other.** `UseSnakeCaseNamingConvention`
  renames what EF named by convention; `IdentityDbContext` names its tables
  *explicitly* with `ToTable("AspNetUsers")` and six more, and an explicit name is a
  decision the convention is right not to overrule. The first run produced
  `transactions` beside `AspNetUsers`, with constraint and index names snake_cased
  either way because those were left to convention -- read out of the running
  database with `\dt`, not guessed at. That is exactly what #13 decided against: a
  capital letter in a Postgres identifier makes it quoted for ever. Seven explicit
  `ToTable` calls in `OnModelCreating` fix it, and they are seven lines rather than
  a loop over `GetEntityTypes()` because the loop hides which tables it renames
  behind a `StartsWith` and needs a `ToSnakeCase` this repository would then own a
  second copy of.

  **One context, not two.** A separate `IdentityDbContext` against its own schema
  is what a larger system does and keeps the domain tables clear of the auth
  subsystem. It lost on machinery: two connection strings' worth of ceremony, two
  migration histories, `--context` on every `dotnet ef` invocation, and a second
  `efbundle` in the deploy job, for a table count in single figures.

  **The bug that reached a running application, and the reason it is the most
  important paragraph here.** `AppDbContext` captured `currentUser.OwnerId` into a
  string field in its constructor, on the reasoning that a context lives for one
  request and is resolved when the endpoint's arguments are bound -- after
  authorization. That reasoning is wrong the moment Identity is in the pipeline:
  the cookie handler validates the security stamp during `UseAuthentication`, which
  resolves `SignInManager` -> `UserManager` -> the EF store -> **this context**. So
  it is built while `HttpContext.User` is still anonymous, and because a scoped
  service is created once, the captured null stays null for the whole request.

  What that looked like from outside is the half to remember. Every read answered
  `WHERE owner_id IS NULL` and every write stamped null, so two accounts saw one
  consistent, plausible, shared list -- with no error anywhere, and every unit test
  green, because they all construct the context with the owner already known, which
  is the one thing production does not do. **A filter that fails to nothing is
  loud; this one failed to everything.** It was caught by registering two users and
  sending requests, which is the discipline #19 put in the review rules and #23,
  #35 and #39 have each paid for since.

  The fix is to hold the service and read the property inside the expression, which
  is still parameterised -- EF evaluates `_currentUser.OwnerId` client-side at
  execution and passes it as a parameter, so one compiled query stays correct for
  every user. Only *when* it is read changes.
  `The_owner_is_read_when_the_query_runs_and_not_when_the_context_is_built` is the
  regression test, and it was checked by mutating the code back and watching it
  fail, per #21.

  **The error keys have to be camelCase**, and this one also only appears by
  sending a request. `ValidationFilter<T>` runs every member name through
  `JsonNamingPolicy.CamelCase.ConvertName`, and the client matches the key against
  its own field names to decide where to put the message. Written as
  `nameof(request.Password)` and left alone, the server answered `"Password"`, the
  form found no field by that name, and the sentence appeared in the banner at the
  top instead of under the password box: correct, visible, and in the wrong place.

  **Tests: 105, and they still need no Postgres, no Docker and no network.** That
  property is #22's and it survived, but not for free -- registering and signing in
  reach `UserManager`, so those cannot be tested in process without either a second
  EF provider whose behaviour is not Postgres's or a database container in CI. What
  is automated: every anonymous refusal, that no refusal is a redirect, that the
  shell is served while signed out, that sign-in endpoints are anonymous, that the
  process starts with nothing configured, the query filter's SQL, and
  `RegistrationPolicy`, which is a pure function and is where the invite decision
  actually lives. What is not is written at the top of `AuthorizationTests` in as
  many words, and was verified by hand against the compose database: register,
  sign in, wrong password, unknown user, lockout, sign out, and two accounts that
  cannot see each other.

  **The Data Protection keys were not persisted, and that was closed on 2026-08-30
  in #88.** The paragraph here used to end "this is the one item on this list most
  likely to be worth fixing next", and it was: the authentication cookie was
  encrypted with keys generated in memory, they died with the process, and with
  `--min-replicas 0` that is roughly every fourteen idle minutes (#35) -- so coming
  back after a pause meant **typing a password**, which was a good deal worse than
  it was under OpenID Connect, where a live provider session made it a redirect and
  no typing. Left out of #52 because it is a deployment decision with a bill
  attached rather than part of closing the door, and the bill turned out to be a
  fraction of a US cent a month. The account is the #88 entry at the end of this
  list.


- **Database: Postgres. A container locally, Azure Database for PostgreSQL
  Flexible Server once deployed -- decided 2026-08-25** (#34). This replaces the
  sentence that stood here before, "Azure Database for PostgreSQL is not free;
  while learning, Postgres runs as a container next to the app", which was
  written when there was nowhere to deploy to. It described a preference rather
  than an arrangement, and slice 3 needs an arrangement: the connection string,
  the migration step of #37 and the monthly bill all hang off it.

  `docker-compose.yml` is untouched and stays the development database. What
  changes is that **"it works locally" and "it works deployed" stop being the
  same sentence** -- two Postgres instances, two versions to keep equal, two
  sets of credentials, and only one of them has a firewall in front of it.

  **The shape:** Burstable `Standard_B1ms` (1 vCore, 2 GiB), 32 GB storage,
  high availability disabled, PostgreSQL 17 to match the local
  `pgvector/pgvector:pg17` of `docker-compose.yml`.
  **Free for twelve months**, because the Azure free account includes 750 hours
  a month of B1ms plus 32 GB storage and 32 GB backup, and 750 hours is more
  than a month has -- so it is a database running continuously, not a quota to
  ration. Eligibility is "never had an Azure subscription": the subscription
  does not exist yet and gets created in #35, so **the clock starts there and
  there is exactly one of these to spend**.

  **What it costs when the twelve months end**, which is the part to have
  written down before it arrives rather than after: roughly **15-20 USD a
  month**, being about 12 for B1ms compute and 4-5 for 32 GB of storage,
  region-dependent. That figure is off published pricing pages on 2026-08-25 and
  not off the calculator with a region selected, so treat it as the order of
  magnitude and not the bill. The lever if it matters:
  `az postgres flexible-server stop` halts **compute** billing while storage
  keeps billing -- and the server **starts itself again after 7 days**, so it is
  a recurring manual act or an automation runbook, not a setting.

  **The database does not scale to zero, and that asymmetry is deliberate.**
  Scale-to-zero is why Container Apps was chosen for the *app*; B1ms is always
  on and always billed. #34 phrased this as "`min-replicas` for the database
  cannot be 0", and a managed server satisfies it by construction -- there is no
  replica count to set wrong. Worth keeping the distinction the phrasing hides:
  the failure it warns about is a *container replica* being descheduled, which
  drops connections and, without a volume, the data. A serverless Postgres that
  suspends its compute and wakes on the next connection is a different thing and
  is not what that rule forbids.

  **Three things that lost.**

  *Postgres as a second container app*, which reads as the cheap continuation of
  the compose file. The route that would have made it viable is gone: the
  Container Apps **dev-service add-on was retired on 2025-09-30**, and
  Microsoft's own guidance is to move to a managed service. What is left is a
  Container Apps volume, which is Azure Files and therefore SMB -- Postgres's
  durability assumptions about `fsync` and file locking are not what that
  protocol offers -- or no volume at all, where the data lives on the replica's
  disk and is gone at the next revision. That second one is the worst failure
  shape available here: slice 1's acceptance test, "a transaction survives a
  restart", stays **true locally and false deployed**, with nothing reporting
  the difference.

  *Neon's free plan.* 0.5 GB of storage, 100 CU-hours a month, compute suspends
  after five minutes of inactivity and **cannot be told not to** on the free
  plan, waking in roughly 300-500 ms. Genuinely free with no twelve-month
  fuse, genuinely used in the industry, and comfortably inside every limit for
  one person entering transactions weekly. It lost on what this slice is *for*
  rather than on anything technical: moving the database out of Azure removes
  exactly the half of it worth learning -- a firewall rule or a delegated
  subnet, enforced SSL, managed backups, and a bill that arrives. It is also the
  cost of a second vendor, a second dashboard and a connection leaving Azure for
  the public internet. **This is the answer to reopen when the free year ends**
  and the honest question is whether a spending tracker one person uses weekly
  is worth 15-20 USD a month.

  *Supabase's free plan.* 500 MB, and it **pauses the whole project after a week
  with no API requests**, resumed by hand. For an application used weekly that
  is a coin flip, and it fails as "the site is down" rather than as "the first
  query is slow", which is the worse of the two.

  **The networking trap #35 walks into, named now.** A Container App on the
  Consumption workload profile has **no stable outbound IP**, so a Flexible
  Server firewall rule pinned to an address does not hold. The two honest routes
  are "Allow public access from any Azure service within Azure" -- a 0.0.0.0
  rule, which admits every Azure tenant's resources and not merely this
  subscription -- or VNet integration with a delegated subnet and private
  access, which is more to set up and is what a production system would do. A
  static egress IP needs a NAT gateway, which needs a workload-profiles
  environment rather than a Consumption-only one, so it is not a checkbox.

  **`pgvector` is available and is not spelled that way.** `docker-compose.yml`
  runs `pgvector/pgvector:pg17` on purpose, so slice 5 does not force recreating
  the volume. Flexible Server has the extension on every compute tier including
  Burstable, but it must be allowlisted in the `azure.extensions` server
  parameter, and **the name to put there is `vector`** -- `pgvector` is what the
  community calls it, `vector` is what the binary and `CREATE EXTENSION` are
  called. Nothing to do until slice 5; worth setting when the server is created,
  since it is a parameter change and a restart rather than a data migration.

  **The schema reaches the deployed database as `efbundle`, decided 2026-08-25**
  (#37). `dotnet ef migrations bundle --self-contained -r linux-x64` produces one
  executable holding the migrations, EF Core, Npgsql and the runtime; `ci.yml`
  builds it in the `build` job from every commit and uploads it as an artifact,
  and step 13 of `docs/deploy-azure.md` is how it is run. 128 MB, against 33 MB
  without `--self-contained`.

  **What lost.** `dotnet ef database update` from the runner -- the obvious one,
  and it needs the SDK, the pinned tool and the source checked out in a job whose
  only business is deploying. `dotnet ef migrations script --idempotent` -- SQL
  that can be read before it runs, which is genuinely what a reviewer would want,
  and it loses on who executes it: `psql` is another dependency to install and
  another place the connection string has to arrive. Neither is wrong; the bundle
  is the one that needs least where it lands.

  **`Database.Migrate()` on startup stays out**, and `Program.cs` carries the
  reason beside the `AddDbContext` call because the absent line is the surprising
  one. Three reasons, and the third is the one that decides it: with
  `--min-replicas 0` a cold start would run migrations on a path that already
  costs 23.3 s; several replicas start together and each would run them; and a
  migration that throws in there throws before `app.Run()`, so the container
  exits, restarts, exits -- **an application that will not start, from a
  deployment that reported success**. As a deployment step the same failure is a
  red step with the SQL error in it and the previous revision still serving. What
  it costs: the app will now start against a schema older than its model and fail
  at the first query. Nothing checks that, deliberately -- a version check at
  startup is the same coupling in a smaller coat. Worth knowing that concurrency
  was never the sharp end: EF Core takes `LOCK TABLE "__EFMigrationsHistory" IN
  ACCESS EXCLUSIVE MODE`, seen in the bundle's own output, so parallel starts
  serialise rather than collide -- they just all wait.

  **Building the bundle needs a connection string, and #37 did not predict it.**
  `dotnet ef` starts the application's host to find the `DbContext`, so
  `Program.cs` runs and throws on a missing `ConnectionStrings:Default`. It never
  connects -- `Host=build-time-only` is enough. It does not fail on a developer
  machine because `dotnet ef` applies the first profile in `launchSettings.json`,
  that profile sets `ASPNETCORE_ENVIRONMENT=Development`, and Development is what
  loads user-secrets; a runner has none, so the command that works locally
  answers `Unable to create a 'DbContext' of type 'AppDbContext'`.

  **The connection string reaches the bundle by environment, never by
  `--connection`.** The argument exists and is the first thing the bundle's
  `--help` lists; it also puts the password in the process list. Measured
  instead: the bundle runs the application's own configuration pipeline, so
  `ConnectionStrings__Default` reaches it the same way it reaches the app. Which
  answers #37's "two places holding one secret" trap properly -- there is still
  one place, the Container Apps secret from #36, read back with
  `az containerapp secret show` at deploy time rather than copied into GitHub.

  **A migration is atomic; a run of migrations is not** -- measured on a
  throwaway database with a deliberately broken migration, not reasoned about.
  Postgres has transactional DDL and Npgsql wraps each migration in its own
  transaction, so the failing one leaves nothing behind, while the ones before it
  are applied and recorded. **So the answer is fix forward, not restore from
  backup:** because `__EFMigrationsHistory` is accurate, a corrected bundle
  re-run resumes at exactly the migration that failed. Restore-from-backup is
  what a migration that *succeeded* and destroyed data needs, which is a
  different accident. The shape this does not cover is DDL Postgres cannot run in
  a transaction -- `CREATE INDEX CONCURRENTLY` first -- and there is none yet.

  **Migrate first, then deploy the revision.** So the old revision briefly runs
  against the new schema, which is safe while every migration only adds -- code
  that has never heard of an index is unaffected by one existing. The other order
  puts the *new* revision against the *old* schema, which for an added column is
  a query naming a column that is not there: strictly worse for the changes this
  project makes. Neither order saves a rename; that needs expand-and-contract,
  and nothing here does yet.

  **`ix_transactions_occurred_at_created_at`, and it is not `IsDescending()`.**
  The list query sorts `OccurredAt DESC, CreatedAt DESC`, so copying the LINQ
  would have written a descending index. A btree is walked backwards just as
  cheaply and a descending index only pays when the directions are mixed;
  measured with `SET enable_seqscan = off`, the plan is `Index Scan Backward
  using ix_transactions_occurred_at_created_at` with no sort step. The index is
  justified by the shape of the one query this application makes, not by the row
  count -- at a few thousand personal transactions Postgres will read the table
  and sort it, and be right to.

  **The schema is `snake_case`, decided 2026-08-18** (#13), applied by the
  `EFCore.NamingConventions` package -- one call to
  `UseSnakeCaseNamingConvention()` beside `UseNpgsql`. PascalCase is the EF
  default and is what SQL Server projects keep; on Postgres it makes every
  table a quoted identifier forever, and the Python service in slice 4 would
  meet column names nobody else in that ecosystem writes. The package is
  maintained by the Npgsql lead, so it is the same author as the provider
  already in use. What lost: fifteen lines in `OnModelCreating` walking the
  model and renaming by hand -- no dependency, and it shows how EF metadata is
  shaped, but it is written once and never reopened.

  **`OccurredAt` is a `DateOnly`, decided 2026-08-18** (#17), mapped to a
  Postgres `date`. `timestamptz` stores an instant, and an instant is only a day
  once a timezone is applied: 01:00 in UTC+3 is stored at 22:00 UTC on the day
  before, so a report grouped in UTC and the same report grouped in the viewer's
  zone disagree. Dropping the time removes the question rather than answering
  it, and it matches how the value is made -- typed by hand, weekly, by someone
  who does not recall the minute. What lost: keeping the instant and fixing one
  reporting timezone (more data, but `AT TIME ZONE` then has to appear in every
  query that counts by day, and is silently wrong when forgotten); and storing
  the original offset in a second column (the faithful answer for spending
  recorded abroad, and an extra write on every row for a distinction this
  application never reports on). `CreatedAt` keeps `timestamptz`: it is a
  machine's audit fact, and precision is its whole point.

  The surprise worth keeping: the convention renames the **columns** of
  `__EFMigrationsHistory` but not the table itself, which the provider names.
  A database created before the change therefore still has `MigrationId`,
  while EF now asks for `migration_id`, and `database update` fails with
  `42703: column "migration_id" does not exist`. Dropping that empty
  bookkeeping table lets EF recreate it. A fresh volume never sees this.

- **The categorizer: a Python service, FastAPI, `uv`, in `src/categorizer/` --
  decided 2026-08-26** (#39). One endpoint, `POST /categorize`: a description, an
  amount and a currency in, `{category, source}` out. `GET /health` beside it.
  46 MB image, two stages, non-root uid 1654 to match the .NET one.

  **`categories.py` and `rules.py` moved here out of `evals/` without a character
  changing**, and that move is the substance of the issue rather than a tidy-up.
  A service scored through a *copy* of its own logic reports a number about the
  copy; with one file, the 60.8% macro recall of #25 is a statement about what the
  API answers. Re-run after the move and it is still exactly 60.8% / 62.2% on 45
  rows, which is how the move is known to have been a move.

  **`evals/score.py` reaches the package by `sys.path`, not by installing it.**
  One line, commented, naming `src/categorizer/src`. That keeps
  `python evals/score.py` a command needing no `uv`, no virtual environment and no
  network -- the property that let #25 exist before any of this did. What lost:
  folding `evals/` into the uv project and running it as `uv run evals/score.py`,
  which gives tidier imports and puts a toolchain requirement on the half of slice
  4 that deliberately has none. The cost of the route taken is that the import
  block in `score.py` must not be re-sorted by a formatter -- the path has to be
  set before the `from categorizer...` lines execute -- and there is no formatter
  configured for `evals/`, which is now a reason rather than an omission.

  **The response carries `source`, and it is the one thing here that cannot be
  added afterwards.** `rules` today, `model` later. A row categorised before that
  field existed can never say where it came from, so the field exists before there
  is a second producer. `Source.MODEL` is declared with nothing producing it, on
  purpose. **The .NET side does not store it** -- see "Open decisions with a
  deadline", which is where the argument and the deadline for that live.

  **Abstention crosses the wire as `category: null`, never as `unknown`.**
  `rules.predict` returns that sentinel and `categories.py` keeps it outside the
  vocabulary so the scorer always counts it as a miss; serving the string would
  put a twelfth value into `transactions.category`, which is the exact failure a
  closed vocabulary exists to prevent. The sentinel stops at the HTTP boundary and
  nowhere earlier, so `score.py` still sees it and the number is untouched. What
  that costs: over HTTP, "the rules abstained" and "the service was down" both
  reach .NET as null, and only the first carries a `source` nobody stores.

  **The endpoint handler is `def`, not `async def`, and the .NET instinct is
  wrong here.** `rules.predict` is a synchronous substring scan with no I/O;
  declaring it `async` would run it on the event loop and block every other
  request for its duration, where a plain `def` is dispatched to a worker thread
  by Starlette. In C# `async` is the safe default -- in FastAPI it is a promise
  not to block, and this function cannot keep it.

  **`Protocol` is the seam, and it is deliberately not `@runtime_checkable`.**
  `Predictor` takes the whole request and returns the whole response, so an
  implementation names its own `source` rather than being labelled by
  configuration from another file. `@runtime_checkable` would allow `isinstance`,
  and it checks only that the *names* exist -- not the signatures -- so it passes
  an object whose `predict` takes different arguments. A runtime check weaker than
  the static one is worse than none. The fake in `tests/` inherits nothing, which
  is what proves the structural typing.

  **src layout: `src/categorizer/src/categorizer/`.** The doubled name is
  deliberate. A flat layout lets `import categorizer` succeed from the project
  root without the package being installed, so a test can pass against source the
  built wheel does not contain -- and the eval scorer's path insert would point at
  a folder that is both "the project" and "the import root".

  **`httpx2`, not `httpx`, for `TestClient`** -- and this is #22's and #24's
  check-the-current-major lesson in a third ecosystem. From memory it is `httpx`;
  that installs, the tests pass, and Starlette 1.6 prints
  `StarletteDeprecationWarning: Using httpx with starlette.testclient is
  deprecated; install httpx2 instead` on every run. The library said so itself,
  which is the cheap version of the lesson -- the GitHub Action majors had nothing
  that would. **`uvicorn[standard]` lost** on the mirror-image argument: uvloop and
  httptools in exchange for a heavier image and a native dependency with no
  Windows wheel, for milliseconds this service does not spend.

  **`docker-compose.yml` gained two services and a profile.** `docker compose up
  -d` is now postgres **plus categorizer** -- the two things the application talks
  to, with the app still run from the host. `docker compose --profile full up -d`
  adds the app, and is the only arrangement in which the app reaches the
  categorizer **by service name**; a service with `profiles:` is skipped entirely
  unless named, so the everyday loop does not build the .NET image. The
  categorizer's build context is `./src/categorizer` and the app's is the
  repository root -- opposite calls for the opposite reason, the .NET image having
  to reach `src/landmoney.client` while nothing in the categorizer reaches outside
  its own folder. **`evals/` is not in that context and must never need to be:**
  the scorer imports the service, never the other way round.

  **The healthcheck is the interpreter, because `python:3.13-slim` is as bare as
  the aspnet image** -- no curl, wget or nc, measured. Unlike the aspnet one it
  ships something that can do the job, so `python -c "import urllib.request;
  urllib.request.urlopen(...)"` costs no extra layer. This is the first healthcheck
  in the repository that anything reads: `depends_on: condition: service_healthy`
  on the app. The root Dockerfile still has none, and the reason above is
  unchanged -- Container Apps ignores it and probes from outside.

  **`PYTHONUNBUFFERED=1` is not tidiness.** Python buffers stdout when it is a
  pipe rather than a terminal, which is exactly what Docker gives it, so without
  it the last lines before a crash -- the ones saying why -- are lost. The .NET
  image needs no equivalent because `ILogger` writes through.

  **On the .NET side: a typed client, a two-second timeout, and null on every
  failure.** `AddHttpClient<CategorizerClient>`, so the handler underneath is
  pooled and rotated -- the middle ground between a client per request (socket
  exhaustion) and one static client (a DNS answer cached for ever, which under
  compose is live: recreating the categorizer gives it a new address; verified by
  restarting it and watching categorisation resume with no app restart).

  **Two seconds, and the number is chosen against the *broken* case rather than
  the working one.** Measured 2026-08-26: `docker compose stop categorizer` does
  **not** fail fast. The SYN went unanswered rather than refused, so the client
  took the timeout path and not `HttpRequestException` -- so while the service is
  down, every save costs the full timeout, on the path where the user's
  transaction is being written. Anything generous would be paid per save by
  someone who never asked for a category. It is also far below the client's own
  `REQUEST_TIMEOUT_MS` of 10 s, so this can never be what makes the browser give
  up.

  **`catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)`
  is the whole subtlety.** `HttpClient` implements `Timeout` by cancelling, so the
  timeout and the caller's own cancellation surface as the same exception type.
  The `when` clause is what tells them apart -- and the half that matters more is
  the one it lets through: swallowing a real cancellation would carry on and save
  a transaction for a request whose caller has already gone.

  **The length guard is not about the network and is the one that protects the
  promise.** `Transaction.Category` is `MaxLength(100)`; a service answering
  something longer would throw in `SaveChangesAsync` and lose the user's
  transaction to a failed guess about it. `Transaction.CategoryMaxLength` is now a
  `const` so the check and the attribute cannot drift.

  **Categorise before `SaveChangesAsync`**, one write. What lost: saving first and
  updating the row when the answer arrives -- it survives the process dying
  mid-call, and costs two writes, a second code path, and a window where the API
  has answered 201 with a category the database does not have. The tight timeout
  already bounds the window it would protect.

  **Reversed on 2026-09-02 in #92, and the thing that lost above is what this
  application now does.** The argument was never wrong; the number in it changed.
  A save cost 142 ms with the rules behind the port, and about 2.1 s once a model
  was there (#87) -- and that is the *working* case rather than the broken one, so
  no timeout can bound it. The costs listed here are now paid deliberately, and
  the failure the original order protected against is gone rather than traded
  away: nothing between the request and its 201 can fail any more. See the #92
  entry at the end of this list.

  **21 xUnit tests, and they need no server** -- `HttpMessageHandler` is the seam
  the way `IEndpointFilter` was in #21, so "timeout" takes 50 ms and "unreachable"
  needs nothing to be unreachable. What that leaves untested is said out loud in
  the file: that the client is registered at all, that its base address and
  timeout come from configuration, and that the endpoint calls it. Those were
  checked by hand against the running compose stack, which is #39's acceptance
  test.

  **Checked by breaking it, the way #21 checked its suite.** Five mutations, one
  at a time, reverted from a commit rather than from memory -- which is the trap
  #21 recorded and the reason the commit came first. All five were caught.
  Removing the length guard and dropping the `when` clause each killed exactly one
  test. Serving the `unknown` sentinel instead of null killed one. Retyping the
  vocabulary by hand killed three.

  The fifth is the one worth keeping, because it is the mistake somebody would
  actually make: `predict_by_rules(request.description.replace("-", " "))` -- a
  small, well-meaning normalisation applied in the *service* and not in the
  scorer. It looks like an improvement, it changes what the deployed thing
  answers, and it leaves the 60.8% describing code that is no longer running.
  `test_the_endpoint_answers_exactly_what_the_rules_do` is what caught it, and
  preventing that drift is the reason the move happened at all. It computes its
  expectation by calling `predict`, so it knows nothing about which rule wins and
  cannot itself drift.

  **The first deployment after #39 failed, and the cause was a `?? throw` in
  `Program.cs` -- 2026-08-26.** Worth the space because the lesson is general and
  this repository had already written down the specific case and not applied it.

  `Categorizer:BaseUrl` was registered as required config with `?? throw`, exactly
  like `ConnectionStrings:Default` twenty lines above it. **`efbundle` runs
  `Program.cs`** to find the `DbContext`, and in the deploy job it runs from a
  directory holding nothing but the bundle -- **no `appsettings.json`**, which is
  where that key's default lives. So the key was missing, the throw fired, and the
  job died at `Apply migrations`:

      An error occurred while accessing the Microsoft.Extensions.Hosting services.
      Error: Categorizer:BaseUrl is not set.
      Unable to create a 'DbContext' of type 'AppDbContext'.

  **The general rule, which is the thing to keep: every `?? throw` added to
  `Program.cs` is also a deploy-time landmine.** #37 recorded this for the
  connection string ("`dotnet ef` starts the application's host, so `Program.cs`
  runs and throws on a missing `ConnectionStrings:Default`") and #39 added a
  second such line without asking whether the bundle survives it. The record
  existed; applying it is what did not happen.

  **CI could not have caught it as written, and that is structural rather than bad
  luck.** The `build` job *builds* the bundle inside the checked-out source tree,
  where `appsettings.json` exists and every key resolves -- so that step was green
  on the very run whose deploy failed. The bundle only meets the bare directory
  when it is **run**. Green-build / red-deploy is where those two environments
  differ, and no amount of care in the build step closes it.

  **EF says so on every single bundle build**, and it had been scrolling past
  since #37: `Don't forget to copy appsettings.json alongside your bundle if you
  need it to apply migrations.` It is in the green `build` log of the failed run.
  A warning nobody reads is worth exactly nothing, which is the argument for the
  step below rather than for reading harder.

  **What was fixed, and the two keys are now deliberately different.** A missing
  connection string still throws: the application cannot do its job without it. A
  missing `Categorizer:BaseUrl` logs a warning and leaves `BaseAddress` null,
  which `CategorizerClient` reads as "there is no categorizer" and answers null
  without touching the network. That is the same principle #39 is built on --
  every failure of that service becomes a null category rather than a failure --
  extended one step: **a dependency the application is designed to run without
  must not be able to stop it starting.** A value that is present and unparseable
  still throws, because that is a mistake to report rather than a state to
  tolerate, and the bundle never has a value there at all.

  What it costs: a mistyped *key* now degrades silently to no categorisation
  instead of failing loudly, with one startup warning as the only signal. Small in
  practice -- `appsettings.json` ships inside the image, so the key is present in
  every environment that serves a request.

  **A placeholder base address was the obvious alternative and is worse.** It
  starts, and then every save pays the full two-second timeout against a service
  that was never meant to exist. Null is the state that says so.

  **`ci.yml` gained "The bundle must start without appsettings.json"**, which is
  the structural half and matters more than the code fix. It copies the bundle
  into an empty directory and runs it with a deliberately unresolvable host: no
  database, no service container, no network. The bundle is *expected* to fail --
  the assertion is on which failure, because from a distance the two look alike:

      host built, could not connect  ->  "No such host is known."          pass
      host would not build           ->  "Unable to create a 'DbContext'"  fail

  So the exit code is ignored on purpose and the message is the signal, and
  `set +e` is required because the step's shell is `bash -e` and non-zero is the
  expected outcome. **Verified against the artifact that actually broke**: the
  guard fails the build on the pre-fix bundle and passes on the fixed one. A guard
  checked only against the fixed code is a guard that has never been seen to
  catch anything.

  **What went right, and it is the half not to lose in the retelling.** The site
  never went down. Migrate-first ordering (#37) meant the job died at step 5 of 7,
  before `Deploy the revision` -- no new revision, no schema change, revision
  `landmoney--0000004` serving `sha-1310f26` throughout, confirmed with `curl` and
  `az containerapp revision list`. That is the second time that ordering has paid
  for itself on a first attempt, after the OIDC subject failure of #38, and both
  times the failure landed before anything in Azure was touched.

  **Nothing builds or tests `src/categorizer/` in CI**, so `build` can be green
  over a broken service. Left alone deliberately -- #39 did not ask, and slice 5
  already carries "Evals run in CI on every PR", which is the natural home for
  both halves of the Python tree at once.

- **The model behind the port: `AnthropicPredictor`, Claude Opus 5, structured
  output, in `src/categorizer/src/categorizer/` -- decided 2026-08-28** (#59).
  `anthropic_predictor.py` is the adapter, `prompt.py` is what the model is told,
  and `CATEGORIZER_PREDICTOR` picks which implementation `get_predictor` returns.
  One new dependency, `anthropic>=1.0`; the image went from 191 MB to 203 MB.

  **The seam cost nothing, which is the point of #39 having built it first.** The
  adapter names `Predictor` nowhere -- it neither imports nor inherits the port --
  and `get_predictor` was the one line that changed, exactly as `main.py`'s comment
  predicted. That is `Protocol` being structural where a C# `interface` would have
  required `: IPredictor` here and a reference to the module defining it.

  **The version major is load-bearing, for the fourth time after #22, #24 and
  #39.** `anthropic` 1.0 moved the SDK off `httpx` and onto **`httpx2`**, which is
  the same swap the dev group already made for `TestClient`, so both halves of this
  project now agree on one HTTP library instead of pulling in two. An
  `anthropic.Timeout` *is* an `httpx2.Timeout`, and one from the `httpx` package is
  refused at request time rather than at import.

  **`claude-opus-5` with adaptive thinking left on and `effort: "low"`.** The
  effort is the lever, not the thinking switch: `thinking: {"type": "disabled"}` on
  Opus 5 has two documented failure modes -- a tool call written into visible text,
  and `<thinking>` tags leaking into the response -- and lowering effort buys the
  same latency without them. `max_tokens` is 2048 rather than the ~256 a
  classification suggests, because thinking tokens count against that ceiling and
  the answer is one word inside a constrained object.

  **Abstention is instructed, not merely permitted**, and the reason is the metric.
  A model forbidden to decline converts an abstention into a confident error, and
  macro recall charges the same for both while the .NET side stores the wrong one.
  It is also what makes the comparison fair: the rules abstain on 22 of their 23
  misses, so a model that may not abstain is being scored on a different task. The
  sentinel is `unknown` -- `rules.py`'s, reused rather than reinvented, so one word
  means one thing for both predictors and `categories.py` keeps it outside the
  vocabulary either way.

  **The response schema is `output_config.format`, and the adapter validates the
  answer anyway.** The enum is `CATEGORIES` plus the sentinel, enforced by the API,
  so a twelfth category is nearly unreachable through this route -- and the check in
  `_answer_from` is not redundant, because that constraint is a property of one
  route to one API while the check is a property of the adapter. It is also what
  turns `Category("takeaway")` from a 500 into a clean null, which is what #59 asks
  for.

  **Normalisation applies to the answer and to nothing else**, which is #59's first
  trap and #39's caught mutation in a new coat. `Groceries`, ` groceries ` and
  `GROCERIES` all become `groceries`; the *description* reaches the model exactly as
  it arrived, because tidying it here would improve this predictor and silently move
  the baseline it is measured against. There is a test that asserts the typed string
  arrives verbatim. A synonym is not mapped either: `food` is not a category, it is
  an abstention, and a synonym table here would be the adapter answering a question
  the model was asked.

  The `MaxLength(100)` trap answers itself, and it is worth knowing why: membership
  in a closed vocabulary whose longest member is thirteen characters is a far
  tighter constraint than a length check, so nothing over thirteen can leave the
  adapter and it can never be the thing that discovers the column's width.

  **The adapter catches `Exception`, deliberately, where `CategorizerClient` lists
  exactly three.** The two are not inconsistent: anything raised here becomes a 500
  that the .NET client already turns into null, so narrowing it would protect no
  transaction and would only move the failure one process later, spend the round
  trip, and put the traceback in the wrong service's log. `logger.exception` keeps
  the traceback, which is the only thing distinguishing a bug in the adapter from
  the model being unavailable. There is no equivalent of the `when` clause because
  nothing here takes a cancellation token.

  **An unrecognised `CATEGORIZER_PREDICTOR` stops the process**, which is the
  opposite of how `Categorizer:BaseUrl` is treated on the .NET side, and the
  difference is which way the mistake points. There, an absent value had one
  unavoidable cause (`efbundle`) and the failure was a dead deploy. Here `modle`
  would serve the **rules** while the deployment believed a model was running, and
  #60 would record the baseline's number under the model's name with nothing
  reporting it. Blank reads as unset rather than as an error, because
  `${CATEGORIZER_PREDICTOR:-}` and an empty Container Apps variable both arrive as
  an empty string and neither means "refuse to start".

  **A missing key does *not* stop it, and that was assumed wrongly first.**
  `anthropic.Anthropic()` with no credential anywhere constructs cleanly and defers
  the failure to the first request -- so a deployment that selected the model and
  forgot the key starts, serves 200s, and answers `category: null` for ever, which
  is indistinguishable from a model that declines every row. One `logger.error` at
  startup is what turns "silently free" into "findable"; it is not a raise, for the
  same reason `Categorizer:BaseUrl` is not one.

  **Verified against a real 401**, since there is no key on this machine: a
  deliberately broken `ANTHROPIC_API_KEY` produced `anthropic.AuthenticationError`,
  a logged traceback, and `200 {"category": null, "source": "model"}`. That is the
  acceptance test #59 names, and it is the only part of the model path that could be
  exercised without spending money. The request shape was checked against the SDK's
  own types instead -- `OutputConfigParam` has exactly `effort` and `format`,
  `JSONOutputFormatParam` exactly `type` and `schema` -- which proves the parameters
  exist and are typed as sent, and does not prove the model answers well.

  **That last gap closed on 2026-08-28 in #60**, once #76 provisioned a key. The
  request shape was right first time: the first call ever accepted by this
  repository answered correctly, and 106 more followed with **zero failures and
  zero timeouts**. The paragraph above used to end "no request has ever been
  accepted by the API"; it has been, and the sentence is kept in this shape so the
  record reads as a gap that was closed rather than one that was quietly deleted.

  **The model scores 98.9% macro recall against the rules baseline's 56.1%** --
  `claude-opus-5`, `effort=low`, prompt `sha256:c8ad9d9fd16f`, 53 rows, two
  identical runs, one abstention and **zero confident errors**. The `other`
  category, which a substring baseline structurally cannot score at all, went 0/3
  to 3/3. Section 7 of `docs/evals.md` is the full account and is the file to read
  before quoting the number, because the caveat matters more than the size: **the
  eval set was written by Claude and the predictor scored against it is Claude**,
  in English, when real entries would be Russian and Romanian. #47 -- real rows --
  is the only thing that fixes that, and `evals/holdout.csv` is still unlooked-at.

  **That last clause was true for exactly one day, and #91 is what noticed.**
  #66 released the holdout on 2026-08-29 -- section 4 of `docs/evals.md` allows
  it once slice 4 has closed, which it had, with #60 -- labelled its ten rows and
  scored both predictors on them: **rules 44.4%, model 100.0%**, nothing tuned
  afterwards. So the two numbers #91 asks for already existed, and the issue was
  written from the four places that still said otherwise -- this clause,
  `docs/evals.md` sections 4 and 7, and `docs/roadmap.md` -- none of which #66
  went back to amend. The general form is worth more than the instance: **a fact
  asserted in four places is a fact that will be updated in one of them**, and
  the three left behind are the ones a later reader trusts, because they read as
  corroboration rather than as copies of each other. Section 4 is rewritten,
  because it was the definition of the file rather than a record of a day; the
  other three are left standing with the correction beside them, so the record
  shows the gap closing rather than having been quietly edited shut.

  **What it costs is that nothing now stands behind the caveat above.** A holdout
  is spendable once; this one was synthetic anyway, so it could never have
  answered the "Claude grading Claude" question -- only real rows can. The
  replacement is a slice of #90's export **held back before the labelling
  session**, agreed 2026-09-02, and that ordering is the whole of it: once a set
  has been scored against, carving a holdout out of it retroactively produces
  rows that were already seen.

  **Measured at the 6-second timeout the service actually uses**, not a relaxed
  one, at ~2.1 s per call. That was a decision rather than a default: a number
  produced under a configuration that is not deployed describes something that does
  not exist. It also means the timeout has now been shown to have headroom for this
  model at this effort, which is what would have to be re-measured before raising
  `CATEGORIZER_EFFORT`.

  **`evals/baseline.json` still records the rules, deliberately.** It is what CI
  asserts on every pull request, `check` refuses to compare across predictors, and
  the model must never run on a pull request -- one API call per row would turn the
  required check into a bill. The model's number lives in prose in `docs/evals.md`,
  where it can carry its caveats; a JSON file cannot say "the set was written by the
  thing being measured".

  **The fake is a fake *client*, not a fake predictor**, and both exist. The
  endpoint's seam is `dependency_overrides` and was already tested in #39; the
  awkward cases #59 lists -- an answer outside the vocabulary, an empty answer, a
  very long answer, an exception from the client -- are adapter-internal and
  unreachable through that seam. So `AnthropicPredictor` takes its client as a
  constructor argument, which makes "this test cannot reach the network" structural
  rather than remembered: a test that forgot to pass a stub would fail to construct.
  67 Python tests, none of which opens a socket, and the whole suite runs with the
  SDK uninstalled because the import is inside `from_env`.

- **Two timeouts on the categorizer client, 2 s to connect and 8 s overall --
  decided 2026-08-28** (#59), and this is the decision that issue actually turned
  on. #39 gave the whole call two seconds and chose the number against the *broken*
  case: a stopped categorizer leaves the SYN unanswered rather than refusing it, so
  every save paid the full timeout while the service was down.

  A model call does not fit in two seconds, and #59's three routes are all worse
  than they look. Keeping 2 s makes the deployed behaviour "rules or nothing"
  without saying so. Raising the single number re-prices the outage the 2 s existed
  for -- eight seconds per save, every save, while the service is down. Categorising
  *after* the save is architecturally honest, reverses #39's explicit "before
  `SaveChangesAsync`" decision, and needs somewhere to put follow-up work; it is its
  own issue. Splitting them gives the two different failures two budgets, which is
  all they ever needed: `SocketsHttpHandler.ConnectTimeout` for "not there",
  `HttpClient.Timeout` for "thinking".

  **That own issue was #92, taken on 2026-09-02, and it is now the route this
  application takes.** Both budgets stay exactly as they are and now bound a
  background sweep rather than somebody's save, so "every save paid the full
  timeout while the service was down" describes something that no longer happens
  on the request path. The two-budget split is what still makes an outage cheap
  there; it just no longer costs a user anything. See the #92 entry at the end of
  this list.

  **Measured, because the first version of this was wrong twice.** Categorizer up:
  142 ms and a category. Categorizer stopped: **2043 ms**, a 201, and no category --
  so #39's property survives exactly. Both stay under the browser client's
  `REQUEST_TIMEOUT_MS` of 10 s, so neither can be what makes the page give up.

  **What was wrong the first time, and it cost the default in `appsettings.json`:**
  `BaseUrl` was `http://localhost:8000`, and on Windows that name resolves to `::1`
  first, where nothing is listening because compose publishes on `127.0.0.1` only --
  and Docker Desktop swallows the attempt rather than refusing it. The dead IPv6
  attempt ate the entire connect budget, and a save took **the full eight seconds
  and stored no category**, against 156 ms once the key held an address. So the new
  budget made the everyday `dotnet run` loop strictly worse than before it. The fix
  is the address, not a larger number: `.env.example` has carried that exact warning
  for the Postgres port since 2026-08-05, one file away, and the design walked into
  it anyway.

  **The second thing that was wrong: `ConnectTimeout` expiry surfaces as a
  cancellation, not as `HttpRequestException`.** So both clocks land on
  `CategorizerClient`'s `OperationCanceledException` branch, and that branch used to
  log `http.Timeout` -- reporting "did not answer within 00:00:08" for a call that
  gave up at 2.15 s, which sends the reader to the wrong configuration key. It now
  logs how long it actually waited. A log line that misnames which limit fired is
  worse than one that names neither.

  The trade this does not cover, said out loud: a service that accepts the
  connection and then hangs still costs the full eight seconds. That is the right
  way round -- accepting a connection is evidence something is alive.

- **The categorizer is deployed as its own Container App with internal ingress,
  `--min-replicas 0` -- decided 2026-08-28** (#61). `landmoney-categorizer` in
  `cae-landmoney`, a second image pushed by the same `publish` job, a second
  `containerapp update` in `deploy`, and `Categorizer__BaseUrl` set on the app to
  the categorizer's internal FQDN. Step 16 of `docs/deploy-azure.md` is the
  commands.

  **What it fixes is an absence rather than a fault, which is why it lasted.**
  #39 added the service to `docker-compose.yml` and stopped; slice 3 had closed
  before the service existed, so nothing in Azure built, pushed or ran it. The
  deployed app therefore resolved `Categorizer:BaseUrl` to the `appsettings.json`
  default `http://127.0.0.1:8000`, found nothing listening, and stored **every**
  transaction with no category. The fallback of #39 -- a failed categorizer is a
  null category and never a failed save -- is exactly what hid it: nothing was
  ever red. Worth keeping as the general shape, because this project keeps
  choosing that fallback: **a dependency the application is designed to run
  without is a dependency whose absence nothing reports.**

  **What lost: a second container in the same app.** Shared revision, shared
  lifecycle, `localhost:8000` keeps working with no configuration change, one
  thing to deploy -- and it dissolves the cold-start problem below for free,
  since uvicorn would start in parallel with the .NET process. It lost on what
  this repository says it is for: skill gained over working code, and the thing
  worth learning here is service-to-service inside a Container Apps environment
  rather than two processes in one box. The second half of the argument is not
  about learning at all -- a sidecar couples two releases into one, so shipping a
  Python change would replace the .NET revision and sign everybody out, Data
  Protection keys being in memory (#52).

  **`--min-replicas 0`, and the first save of a session is the price.** #61's
  first trap: the app takes 23.3 s to come back from zero (#35), and a
  categorizer that also scales to zero puts a second cold start on the path of a
  save that gives up after 8 s. `--min-replicas 1` is the alternative and keeps
  one replica billed around the clock for a service one person uses weekly, on a
  subscription already facing 15-20 USD a month when the Postgres free year ends
  (#34). Declined as a **choice rather than a discovery**, which is what the trap
  asked for. The categorizer's own cold start is deliberately recorded as *not
  measured*: the image is 46 MB against 350 MB and uvicorn starts in about a
  second, so the pessimism may be unearned, and a number belongs there rather
  than a guess.

  **`https://` to the internal FQDN, and `http://` was written first and is
  wrong.** The reasoning for http reads well -- the hop never leaves the
  environment, so there is no certificate worth validating -- and
  `az containerapp create` sets `allowInsecure: false`, so port 80 answers a POST
  with **`301 Moved Permanently`**, `HttpClient` follows a 301 by re-issuing it
  as a **GET**, and `/categorize` answers **405**. Over https the same request is
  `200 {"category":"transport","source":"rules"}`. Both measured from inside the
  environment, which is the only place either could be.

  Three things to keep from that. The failure would have been **another silent
  null category**, arriving through the change that exists to end silent null
  categories, which is why `ci.yml` asserts the scheme and not only the host.
  **`GET /health` over http appears to work**, because a redirected GET is still
  a GET -- the health check is the one probe structurally unable to reveal this,
  and a smoke test built out of it would have passed. And the certificate
  validates with nothing configured, so the tempting shortcut of disabling
  validation when https is refused is never needed here.

  **An internal service is not unobservable, and `az containerapp exec` is the
  door.** The categorizer's image ships a Python interpreter, so a request can be
  sent from a live replica **to the internal FQDN** rather than to `localhost` --
  same DNS, same ingress, same method the app uses. That is how the 301 was
  found, and it is the technique rather than a one-off. What it cannot show is
  the .NET half: that the aspnet image trusts the same chain, and that the call
  fits the 8-second budget. Only a save through the site shows that.

  **`Lidl` is not a rule, and #61's own acceptance test said to use it.** The
  baseline matches ordinary words plus a few merchant names; `Lidl` is answered
  `{"category": null}`, measured. A check whose input produces the failure it is
  written to detect passes nothing and fails everything -- the acceptance test in
  step 16 uses a description that actually matches, and says why.

  **Internal ingress means CI cannot smoke-test it, and that gap is answered
  rather than papered over.** There is no public FQDN, a runner is not inside the
  environment, and the one process that is -- the app -- has no endpoint
  reporting on its dependencies and would need a signed-in session anyway. So
  `Check the categorizer` asserts the three things that come undone without
  anyone noticing: the revision runs this commit's image, the ingress is still
  `external: false`, and the app's `Categorizer__BaseUrl` is exactly the internal
  FQDN read back from Azure. The fourth question -- does it answer -- is the
  by-hand acceptance test in step 16. **The `Categorizer__BaseUrl` assertion is
  the one that matters**: it is the only automated thing standing between this
  and the silent state described above.

  **CI replaces images; the runbook creates resources.** The deploy job fails
  with a message naming step 16 when the app is absent, and deliberately does not
  create it: a create-if-missing would put internal ingress, the replica counts
  and the cpu/memory into two places at once, and would quietly resurrect an app
  somebody deleted on purpose. The cost is a **one-time red run**, by
  construction and not by accident -- step 16 needs an image that only `publish`
  on `main` can produce, so the first run after #61 merges cannot find the app.
  The categorizer steps are therefore **last** in the job, after the app is
  deployed and verified, so that first failure leaves nothing half-applied.

  **The gha cache needed scopes, and forgetting them is silent in the familiar
  way.** Two `docker/build-push-action` builds in one job share the cache
  backend's default scope (`buildkit`), so the second imports the first's layers,
  misses on all of them, and exports over them -- no error, and the only symptom
  is that neither build is ever faster. `scope=app` and `scope=categorizer`.
  This is the same failure shape as #24's missing `setup-buildx-action`, one
  cache setting along.

  **The deployed categorizer ran `rules` until 2026-08-30**, written out rather
  than defaulted, so `az containerapp show` could answer the question;
  `CATEGORIZER_PREDICTOR=model` meant an `ANTHROPIC_API_KEY` secret and one Claude
  call per saved transaction, which was a decision with a bill and was not #61's.
  **That decision is #87**, below. The unauthenticated endpoint being internal-only
  is the other half of it and is unchanged: an open categorizer with a model behind
  it is somebody else's Anthropic bill, which is why #87 does not widen the ingress
  by so much as a flag.

- **CSV import: one endpoint, `POST /api/transactions/import`, taking a raw
  `text/csv` body -- decided 2026-08-28** (#62). `src/LandMoney.Web/Import/` holds
  the reader, the four-column parser and the duplicate key; the screen is
  `src/landmoney.client/src/components/ImportForm.tsx`. 64 new tests, and they
  still need no Postgres, no Docker and no network.

  **Why now, and it is not about the .NET side at all.** The eval set is 53 rows
  written by Claude, and `docs/evals.md` section 7 says the caveat matters more
  than the number: the set was written by the thing being measured. #47 -- real
  rows -- is the only thing that fixes it, and the reason it has not happened is
  that a year of history is a lot to type. This is the short way round. The .NET
  slice rule still holds: this is one endpoint and one file input, and it exists
  to feed `evals/`, not to grow the application.

  **`text/csv` as the raw body, not `multipart/form-data`, and this is the
  decision the whole shape hangs off.** Multipart is what every tutorial shows and
  what `IFormFile` binds. It loses on security rather than on convenience:
  `AuthenticationSetup.cs` records **two** CSRF locks -- the `SameSite=Lax` cookie,
  and the JSON content type a cross-site form cannot set without a preflight this
  server never answers -- and `multipart/form-data` is one of the three types a
  plain cross-site `<form>` can produce, so a multipart endpoint would silently
  keep only the first. `text/csv` is not form-submittable, so both survive. **The
  `Content-Type` check in the handler is therefore a control and not tidiness**,
  which is written beside it, because relaxing it to accept
  `application/octet-stream` from some client looks like tolerance and is not.

  It also avoids the other half of the cost: minimal APIs apply antiforgery
  validation to any endpoint that binds a form, so `IFormFile` needs
  `.DisableAntiforgery()` **and** `app.UseAntiforgery()` -- machinery #52
  deliberately left out. What the raw body gives up is the filename, which nothing
  here wants, and the browser sends the `File` object directly with no `FormData`.

  **The rules are `CreateTransactionRequest`'s, run through the same `Validator`
  call `ValidationFilter<T>` makes.** So a row read out of a CSV is judged by
  exactly the rules a row posted as JSON is judged by, the messages are written
  once, and the two ways into this table cannot drift. Both arguments of that call
  are load-bearing and fail silently if dropped -- `validateAllProperties: true`
  or every rule but `[Required]` is skipped, and `HttpContext.RequestServices` or
  `PlausibleDateAttribute` never finds its `TimeProvider`. That is #21's paragraph
  arriving at a second call site unchanged.

  Which also answers, rather than edits, the comment on
  `CreateTransactionRequest.MaxYearsBehind`: it predicts this feature by name --
  "the number to revisit first if CSV import of old statements ever arrives" -- and
  five years comfortably holds a year of history. Measured rather than assumed: a
  row dated 2016 comes back as `Date cannot be earlier than 2021-08-28`, which is
  the rule working through the import path with an `InvariantCulture` date, and is
  the same bound the form applies.

  **`NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint`, and the
  absence of `AllowThousands` is the entire mechanism.** The decision recorded for
  #62 is that a file written with a comma for the decimal point is not supported.
  `NumberStyles.Number` -- the obvious constant, and what `decimal.Parse` defaults
  to -- includes `AllowThousands`, and under `InvariantCulture` that reads `"1,50"`
  as **one hundred and fifty**, silently. That is #31's failure in a new coat, and
  it is the one mutation in this change that would have shipped looking correct.

  `AllowLeadingSign` is in for the mirror-image reason. A bank export writes a
  debit as `-412.50`; parsing it and letting `[Range]` refuse it produces *"Amount
  must be between 0.01 and ..."*, which names the real problem. Refusing it in the
  parser would say *"not a number"* and send the reader hunting a typo in a field
  that reads perfectly.

  **What a Romanian export really does is fail on field count, not on the number**,
  and that is worth knowing before somebody reads the message. An unquoted `1,50`
  is two CSV fields, so the row has one too many and the message says so. The
  number path is only reached when the amount is quoted. Both are refused, which
  is the decision; they are refused with different sentences, which is not obvious
  from the decision.

  **Dates are `ParseExact` against `yyyy-MM-dd` and a timestamp is refused rather
  than truncated.** #62 asks for truncation to be deliberate; declining to convert
  is the most deliberate form available. The honest reason is that the file states
  no zone, so there is no correct day to derive from `2026-07-05T14:33:00` -- which
  is #17's argument for `DateOnly` arriving at the import boundary rather than at
  the storage one.

  **Encoding is strict UTF-8 with the BOM stripped by hand, and the obvious
  implementation is the one that fails silently.**
  `new StreamReader(stream, strictUtf8, detectEncodingFromByteOrderMarks: true)`
  reads well and swaps in its own `Encoding.UTF8` -- the *replacing* one -- the
  moment it sees a UTF-8 BOM, which is exactly what a spreadsheet writes. The
  strict instance would then be discarded in the commonest case, and a cp1251
  description would arrive as replacement characters: imports fine, reads as
  nonsense, never categorised, which is #62's encoding trap word for word.
  Stripping `EF BB BF` and calling `UTF8Encoding(false, throwOnInvalidBytes: true)`
  keeps one decoder for every input, and the correctness of the function then does
  not depend on an internal detail of `StreamReader` at all. A UTF-16 BOM is named
  separately, because "not valid UTF-8" is true and points at the wrong fix for
  Excel's "Unicode Text" export.

  **Duplicates are detected, skipped and named per row**, keyed on day, amount,
  currency and description, in one query bounded by the file's own date range --
  not one query per row. The owner filter is applied by `AppDbContext` without the
  query asking, and `ix_transactions_owner_id_occurred_at_created_at` covers the
  predicate.

  **The decimal scale in that key is load-bearing and is not obvious.** Postgres
  returns `numeric(18,2)` as `78.50` while the CSV says `78.5`; those are different
  bit patterns. `decimal.Equals` compares values and `decimal.GetHashCode` is
  normalised to match, so a `HashSet` lookup agrees -- and if it did not, the
  failure would be a **silent double-import**. Asserted in a unit test on both
  halves of the contract, and then measured against the running application: the
  same file sent twice skipped all seven rows including that one.

  **What that costs, said out loud in the response text as well as here:** two
  identical real purchases on one day -- two 38 MDL espressos, same shop, same
  description -- are one row after an import. What lost: importing everything and
  reporting a count, which never loses a real repeat and silently doubles the table
  when a file is sent twice. Neither is free; this one fails in the direction that
  is visible and correctable, because the response names the line it skipped and
  the form is still there.

  **The import does not call the categorizer, and the screen says so.** #39
  categorises before `SaveChangesAsync`, one call per transaction; #59's measured
  broken case is 2.15 s per save against a categorizer that is not there, so a
  300-row file would be a request that legitimately runs for minutes. What lost: a
  batch endpoint on the Python service, which is the honest fix and is a change to
  a service #61 had only just deployed. So every imported row has a null category
  and a null source, and the response reports how many -- because this is the third
  time this project has chosen that fallback and **a dependency the application is
  designed to run without is a dependency whose absence nothing reports**. Here it
  is reported. The backfill is its own issue.

  **A file-level failure and a row-level failure are different things and are
  answered differently.** A missing header column, a duplicated one, an empty file
  or a quote that is never closed refuses everything with a 400: nothing can be
  done with any row. Anything wrong with one row is an entry in `problems` and the
  other rows still import, which is #62's second acceptance test. `CsvFormatException`
  exists only for the first kind, which is why it is deliberately narrow.

  **`CsvReader` returns a list rather than a `yield return` iterator**, and the
  reason is that exception rather than performance. A deferred iterator throws
  during enumeration -- here, halfway through the endpoint's loop, after some rows
  had already become entities, from a `foreach` that looks like it only reads.

  **CsvHelper lost, and it is the better-tested library.** It lost on the
  dependency rule against a genuinely small scope: the whole of RFC 4180 is quoted
  fields, doubled quotes, and two line endings, and `CsvReaderTests` names each of
  those. The moment this has to read a dialect -- semicolons, an escape character,
  a per-file encoding -- CsvHelper is the right answer and the hand-written reader
  is the thing to delete.

  **`REQUEST_TIMEOUT_MS` gained a per-call override**, which is a change to the
  file every request in the client goes through. An import reads a file, validates
  every row, queries a date range and inserts in one transaction, and on the
  deployed app may also pay the 23.3 s cold start of #35 -- ten seconds is not that
  budget. Raising the constant for everything was the alternative and loses for the
  reason #35 already wrote down: a longer timeout makes a genuine hang take longer
  to report, which is the failure the timeout exists to catch.

  **Checked by breaking it, per #21: eleven mutations, one at a time, reverted from
  a commit rather than from memory.** All eleven caught. Two are worth keeping.
  Writing `NumberStyles.Number` in place of the two flags killed two tests, which
  is the point of the theory that quotes its amounts. And mutating `IsBlank` killed
  only the blank-line test and **not** the trailing-newline one -- because a single
  trailing newline never reaches `IsBlank`; it ends the last real row, and the
  three-condition guard at the end of `Read` is what stops a phantom row. The
  comment claimed both. It was wrong, and only the mutation said so.

  **Verified against the running compose stack, which is where the interesting half
  is.** Seven rows imported with amounts and dates matching the file exactly,
  including `78.5` stored as `78.50` and a quoted `lidl, centru` kept whole. The
  same file again: nothing imported, seven skipped, each naming its line. A
  ten-row file with eight deliberate faults: two imported, one skipped, seven
  refused, and every message named the right rule. A cp1251 file refused by name; a
  BOM-prefixed UTF-8 file with a Cyrillic description imported intact.
  `multipart/form-data` and a missing content type both 415; anonymous 401.

  **And the #52 check, which is the one that has caught a real bug before.** A
  second account sees none of the first account's rows, and importing the
  *identical* file as that second account imports all seven rather than skipping
  them as duplicates. That is the global query filter scoping the duplicate query
  without the query mentioning ownership -- the property that would have failed
  silently, in the direction of one person's import being silently swallowed by
  another person's data.

  **What is still not automated, said plainly.** Everything above about the
  endpoint was done by hand: the handler needs a signed-in session, which needs
  `UserManager`, which needs the database, which is the same wall `AuthorizationTests`
  documents. What the suite does cover is the endpoint being inside the group
  `RequireAuthorization` is applied to, and every pure function underneath it.

- **Correcting a category in the interface: `PATCH /api/transactions/{id}`, a
  dropdown of the eleven, and a badge naming the source -- decided 2026-08-28**
  (#63). `src/LandMoney.Web/Api/Categories.cs` holds the vocabulary and the three
  sources; the screen is
  `src/landmoney.client/src/components/CategoryCell.tsx`. 31 new tests, and they
  still need no Postgres, no Docker and no network.

  **What it is for is not the screen.** A correction made by a person is a
  labelled row, produced by the one user who can judge it, during ordinary use --
  and every other route to labelled data in this project is somebody sitting down
  to do a chore. #62 was the same argument approached from the other side.

  **The vocabulary now exists in two places, not three, and that was the decision
  the issue actually turned on.** #63 said to decide how the copies stay in step
  or accept the drift out loud. The client's copy is gone: `GET /api/categories`
  serves `Categories.All` and the dropdown renders whatever it is given, so the
  screen cannot offer a category the server would refuse. The two that remain --
  `categories.py` and the C# array -- are pinned by
  `CategoriesTests.The_vocabulary_is_the_one_the_categorizer_knows`, which reads
  the Python file (located by `[CallerFilePath]`, not by the runner's working
  directory) and compares the **sequence**, since the order is display order and
  categories.py says so.

  What lost: three copies with a comment on each, which is cheapest and leaves
  live the failure a closed vocabulary exists to prevent -- a person labelling a
  row with a word the scorer then rejects. And one shared data file all three
  read, which is the only route with no copies at all and breaks #39's decision
  that nothing in `src/categorizer` may reach outside its own folder, that being
  its Docker build context. A code generator lost to a test on the same argument
  every generator loses on here: it is a build step in three places for eleven
  strings that change about once a year.

  **A JSON PATCH with one field, and the omission is the control.** #63:
  do not send the whole transaction back to save one field, because a PATCH that
  accepts an amount is a way to overwrite money with a stale value from a screen
  somebody left open. `UpdateCategoryRequest` has exactly `Category`, so there is
  no amount to lose.

  **`required string?` is what answers the usual PATCH ambiguity**, and it is the
  serializer doing it rather than the handler. System.Text.Json enforces
  `required` while binding, so `{}` is a 400 before the type reaches the handler,
  while `{"category": null}` is legal and means clear it. Measured: the 400 names
  the missing property. The alternative is a `JsonElement` and a hand-written
  check for `JsonValueKind.Undefined`, which is the version of this every guide
  shows.

  **Clearing sets both columns to null, keeping #59's invariant** -- a source
  exists exactly when a category does. What lost, and it is a real loss written
  down rather than a tidy answer: a row a person deliberately cleared is
  afterwards indistinguishable from one nothing has ever touched, so a future
  backfill would re-predict over somebody's "I do not know either" -- a hole in
  the never-overwrite rule this same issue asks for. Storing
  `category = null, source = human` is the shape that records it and it lost on
  turning a property checkable in one line of SQL into a special case every later
  query has to know about. **Reopen it the day something re-categorises existing
  rows**, which is the change that makes the hole cost anything.

  **The never-overwrite rule is a call, not a sentence in a closed issue.**
  `CategorySources.MayOverwrite` is trivially true at its one call site -- the
  transaction is constructed thirty lines above and has no source -- and exists
  because that is the state in which a rule is easiest to lose. It is what a
  backfill gets copied from, and `CategorySourcesTests` is what stops it being
  deleted as dead code. Note which way the null goes: an unset source is a row
  nothing has claimed, so a prediction may have it.

  **404 and never 403 for another account's row**, and no ownership check appears
  in the handler: `AppDbContext`'s global query filter means the row is not found
  at all. Verified with two accounts -- B correcting A's row is a 404, B's list is
  empty, and A's row is untouched. That is #52's check, which is the one that has
  caught a real bug here before.

  **The correction does not reload the list, and that is the trap the issue
  names.** `handleCreate` asks the server for the whole list again and argues for
  it: the sort order is the server's and a back-dated entry belongs in the middle,
  so inserting client-side would mean writing that comparator a second time in
  another language. None of it applies here -- a correction changes neither sort
  key, so the row cannot move, and the response carries the stored row. Replacing
  it in place is not a guess. The reason it matters more than for a create is not
  that a blank table is uglier: a create is followed by an empty form, and a
  correction is followed by looking at the row to see whether it took. Measured:
  five rows on screen throughout, and no `.list-status` at any point.

  **The optimistic update is honest about failing.** `CategoryCell` holds the
  chosen value only while the request is in flight, because a controlled select
  that snaps back and then changes again reads as the click not registering. On
  failure it is dropped, so the select visibly returns to what is stored. Measured
  with the API stopped: the value reverted to `groceries`, `aria-invalid` went
  true, the badge still said `rules`, and "Could not reach the API." appeared in
  the row rather than in a banner at the top -- which is #52's mislocated-message
  mistake avoided rather than repeated.

  **Nothing asserts what the handler writes**, said plainly because it is the
  invariant this change is most likely to break. `request.Category is null ? null
  : Human` is one line in an endpoint that needs a signed-in session, which needs
  `UserManager`, which needs the database -- the same wall #52 and #62 both
  document. It was verified by hand instead: set, correct, clear, and the two
  columns moved together every time. Extracting the ternary into a testable
  function was the alternative and lost on being indirection around a conditional
  that sits directly beneath the comment explaining it.

  **Verified against the running application, on a second instance.** The
  everyday `dotnet run` was already up and holding `bin\Debug`, so the build went
  to a separate output folder and the instance to port 5199 against the same
  compose Postgres -- which is worth knowing as a technique: it needs
  `--contentRoot` pointing at the project, or there is no `appsettings.json` and
  no `wwwroot`. A row with `source: model` was produced by a **stub categorizer**
  on another port rather than by SQL or by a paid API call, which is the better
  test of the two: it proves an arbitrary source string travels from the service
  to the badge. All three badges then rendered on rows that actually had all
  three, which is what #63 asks for in as many words.

  **The refusals, measured:** a word outside the eleven, wrong case, and the empty
  string are each a 400 keyed `category` whose message lists the eleven. The empty
  string matters because it is what an HTML `<select>` yields for a blank option --
  the client converts it to null before sending, and the server refusing it is
  what says so if that ever stops.

- **Observability for the categorizer: nine named outcomes, a `Meter` nothing
  reads yet, and one summary line per window -- decided 2026-08-29** (#64).
  `src/LandMoney.Web/Categorizing/` gained `CategorizerOutcome.cs`,
  `CategorizerMetrics.cs` and `CategorizerSummary.cs`; the Python adapter gained a
  `model_call` line per call. 42 new tests, and they still need no Postgres, no
  Docker and no network.

  **What it fixes is an absence, and it is the third time this project has met the
  same shape.** #39 chose "a failed categorizer is a null category and never a
  failed save", #61 found that the deployed application had therefore stored
  *every* transaction with no category for weeks with nothing red anywhere, and
  #62 wrote the sentence down again for the import path. The fallback is right and
  it is exactly what hides its own failure. The general form, now recorded for the
  third time: **a dependency the application is designed to run without is a
  dependency whose absence nothing reports** -- unless something counts.

  **`not-configured` is a counted outcome for that reason.** It is what the
  deployed app did on every save between #39 and #61, and a number on that line is
  the difference between "the categorizer answers nothing" and "there is no
  categorizer". The two are one `null` in the database.

  **Nine outcomes, not four, and this is the half of #64 easiest to skip.** The
  issue says the four `catch` branches "already separate exactly these cases", and
  they do not: an abstention, a refused status and an answer that breaks the
  contract are ordinary returns and nothing is thrown. Counting only exceptions
  would have left the normal case invisible and the abstention indistinguishable
  from a failure -- the exact thing the issue's third acceptance test forbids. The
  three that are not exceptions are `abstained`, `refused`, `unusable`; the ninth
  is `abandoned`, the caller's own cancellation, which is rethrown and counted on
  the way past because it is a fact about the browser's ten-second budget rather
  than about the categorizer.

  **Two consumers, one recording path.** Every exit calls
  `CategorizerMetrics.Record`, which writes to a `System.Diagnostics.Metrics`
  Meter *and* to an in-process tally. Nothing reads the Meter today; a metrics
  endpoint -- the second step #64 explicitly defers -- becomes an OpenTelemetry
  package and a line in `Program.cs`, attaching a second listener to the same
  instruments with no call site touched. What lost: hand-rolled counters only
  (cheaper, and makes that later step a rewrite of nine call sites), and the Meter
  only (standard, and answers nothing today on a machine with no Prometheus and in
  a container app with no scrape).

  **The log is the durable record and the counters are a convenience**, which
  falls out of `--min-replicas 0` rather than from taste: this process dies after
  about fourteen idle minutes (#35), so anything in memory describes at most one
  replica's afternoon. Hence windows rather than running totals -- deltas still add
  up across replicas, where "since start" names a moment nothing records -- and
  hence the summary being silent when nothing happened, on a service one person
  uses weekly.

  **`AddJsonConsole` outside Development, and it is not about the categorizer at
  all.** The default console formatter writes *two* lines per entry and renders the
  structured fields into prose; Container Apps forwards stdout a line at a time, so
  one entry arrives in Log Analytics as two rows, neither carrying `Outcome` as
  anything a query can group by. Naming the outcomes consistently would then have
  bought nothing -- the names would be inside sentences. `Indented = false` is
  required for the same reason and not for tidiness. Development keeps the human
  formatter, because there the reader is a person watching a terminal.

  **What the formatter change made visible, and was deliberately not fixed in the
  same pass:** `AuthenticationSetup`'s "no invite code is configured" error uses
  `{Key}` twice in one template, so its JSON row carries the key `Key` twice. A
  parser keeps one of them and nothing breaks, and it is one line in a file this
  change has no other business in -- mentioned rather than fixed, per this file's
  own rule about adjacent problems.

  **The p95 is over every call including the ones that failed**, which is #64's
  second trap answered rather than met: a latency figure covering only the
  successes is precisely the one that hides a two-second connect timeout. Measured
  in a unit test as arithmetic -- ten calls at 10ms and one at 2000ms give a p50 of
  10 and a p95 of 2000, against a mean of 191 that describes no call that happened.
  Nearest-rank, so every number printed is a duration that occurred.

  **The `source` tag is bounded and the log line is not.** A source is a string
  another process chooses, so tagging it verbatim would let a misbehaving service
  mint one time series per request; anything outside `rules`/`model`/`human`
  becomes `other` in the dimension and stays verbatim in the log. That is #64's
  cardinality trap one field along from the description it is written about -- and
  the description never appears in either, on both sides of the wire.

  **Which side is authoritative, since #64 asks for it to be decided rather than
  discovered: the Python service is authoritative for what the model did, the .NET
  client for what the user got.** It follows from what each can observe. A call
  that answers at seven seconds is billed, and is a `failed`/`answered` line in the
  service and a `timeout` in the client -- both correct. A request that never
  arrives is a `timeout` in the client and nothing at all in the service. So "how
  often does the model answer" is `anthropic_predictor.py`'s number and "how often
  did a save get a category" is `CategorizerClient`'s, and neither is a correction
  of the other.

  **Tokens and cost, and no price in the code.** The adapter logs
  `outcome=... model=... elapsed_ms=... input_tokens=... output_tokens=...
  cost_usd=...`, with the cost computed only when
  `CATEGORIZER_PRICE_INPUT_PER_MTOK` and `CATEGORIZER_PRICE_OUTPUT_PER_MTOK` are
  both set. The published rate for `claude-opus-5` on 2026-08-29 is 5.00 and 25.00
  USD per million; writing those two numbers into the file would produce a figure
  that stays confident and becomes wrong, because a price changes without this
  repository noticing and a stale number in a log is worse than an absent one --
  it is believed. Tokens are the fact, the money is the multiplication. A missing
  usage field reads as `unknown` rather than `0`, for the same reason.

  **An unparseable price does not stop the service**, which is deliberately the
  opposite of `main.py`'s unrecognised `CATEGORIZER_PREDICTOR`, and the difference
  is what each mistake costs. There, the wrong value serves the rules while the
  deployment believes a model is running. Here the worst case is one field missing
  from a diagnostic, and taking a categorizer off the air over that would be
  protecting an arithmetic convenience with an outage. Half a price -- one of the
  two set -- is an error line, because silence there looks identical to never
  having tried.

  **The Python half is `key=value` and not JSON**, unlike the .NET half, and the
  asymmetry is on purpose: uvicorn owns the logging configuration in that process,
  so a JSON formatter means a `dictConfig` reformatting every line the server
  writes too. The .NET side took the formatter because its fields become rows in
  Log Analytics; this side has no such consumer today.

  **A `BackgroundService` does not run `ExecuteAsync` inline, and finding that out
  cost four red tests.** `StartAsync` queues it to the thread pool, so a test that
  starts the service and immediately stops it cancels the body before it has
  executed one statement -- and the failure reads as "it logged nothing", which
  sends the reader to the rendering code. The service now writes one line naming
  the interval when it starts, which is both the only place the interval in force
  is recorded and the signal a test waits for. `StopAsync` also does not observe
  ExecuteAsync's exception -- it uses `Task.WhenAny` -- so a summary that threw
  would fail as an empty log; the test helper awaits `ExecuteTask` afterwards to
  surface the real error.

  **Verified against the running stack, which is where the interesting half is.**
  Categorizer stopped, three saves: each 201 in about 2.0 s, and the line reads
  *3 recorded -- 0 suggested, 0 abstained, 3 timed out, **0 unreachable**, p50
  2009ms, p95 2051ms*. That is #64's first acceptance test and the one it says is easy to
  get wrong, since a stopped container leaves the SYN unanswered rather than
  refusing it (#39). Categorizer restarted, two saves the rules decline: *2 recorded
  -- 2 abstained, 0 timed out, p95 18ms*, which is the third acceptance test -- the
  same `null` on the wire, two different numbers here. Then a description the rules
  match: `Categorizer suggested: groceries by rules in 4ms`.

  **Checked by breaking it, per #21: sixteen mutations, one at a time, reverted
  from the commit rather than from memory.** Fourteen were caught; the two that
  were not are the reason the exercise is worth its hour, because both were tests
  that looked like they asserted something.

  Swapping `{Measured}` for `{Calls}` on the summary line passed the entire suite
  -- every test until then had a window where the two numbers are equal, so
  "latency over 3 of them" and "3 recorded" were indistinguishable. The test added
  for it uses a window holding one untimed call, which is exactly the
  no-categorizer state.

  The other is sharper. Deleting the half-a-price check in `_prices_from`
  entirely still passed `test_half_a_price_is_no_price_and_says_so`, because
  `float("")` then raises and the *unparseable* branch logs an error naming the
  same two variables -- so the test was satisfied by the accident that follows the
  rule rather than by the rule. It now asserts which error. **A test that asserts
  only that something was logged cannot tell a rule from what happens in its
  absence.**

  **What is not automated, said plainly.** That the timer fires on its configured
  interval: driving a `PeriodicTimer` needs a clock whose `CreateTimer` is fake,
  which is `Microsoft.Extensions.TimeProvider.Testing` -- the package CLAUDE.md
  keeps out because a frozen clock is six lines. The shutdown report reaches the
  same rendering deterministically, so what the interval alone can break is a
  summary that never arrives, which looks exactly like an application nobody used.
  It was watched by hand for three windows instead.

  **Still open, deliberately: a metrics endpoint**, which #64 names as its own
  step. The instruments exist and nothing scrapes them; the counters die with the
  replica. Also open: nothing counts on the *server* side of the .NET application
  -- there is no `/health` or `/metrics` reporting on dependencies, so the only
  way to ask this application what the categorizer is doing is to read its log.

- **The answer cache: Redis, on the model path and nowhere else -- decided
  2026-08-29** (#65). `src/categorizer/src/categorizer/cache.py` holds the key, the
  entry and the client; `redis:8-alpine` joins `docker-compose.yml`; one new
  dependency, `redis>=8.1`, and the image went from 203 MB to 206 MB. 54 new Python
  tests, none of which opens a socket.

  **This is the day `CLAUDE.md`'s "a database gets added when it has a job" is
  satisfied, and not a day earlier.** There was nothing to cache while the answer
  came from 109 substrings in memory, which is why #65 says out loud that it
  depends on the adapter and must not be started before it.

  **The cache lives inside `AnthropicPredictor` rather than wrapping `Predictor`,
  and that is the decision the shape hangs off.** A `CachingPredictor` decorator is
  the classic answer and it was what the port existed for; it loses because a
  decorator sees only `CategorizeResponse`, and #65's second bullet is that what a
  call cost -- tokens in, tokens out, money -- is recorded **beside the answer**. The
  usage lives inside the adapter and would have to be smuggled out through the port
  to reach a wrapper, which is a port widened for one implementation's benefit. The
  price of the route taken is that `AnthropicPredictor` now has two jobs; what it
  buys, besides the cost, is that "the rules never touch Redis" is structural rather
  than a wiring rule in `main.py` that a later edit could get wrong.

  **Nothing is normalised to build a key, and that is the sharp edge rather than a
  missed optimisation.** `key_for` is a sha256 of the model id, the effort, the
  prompt fingerprint and **the exact string the model is shown**, byte for byte --
  and `_category_for` builds that string once and uses it for both, so the key
  cannot drift from the input even by a well-meaning edit. `LINELLA` and `linella`
  are two keys and two calls. Folding them would be a rule living in the cache path
  and nowhere else, which is the mutation #39 caught by hand wearing a different
  coat: it looks like an improvement, and it silently makes the recorded baseline a
  number about code that no longer runs. The day folding is genuinely wanted it
  belongs in `_user_message`, where the model sees it too and the eval number moves
  in the same commit.

  **The prompt's digest moved into `prompt.py` as `FINGERPRINT`**, computed exactly
  as `evals/score.py` computed it and still printing `sha256:c8ad9d9fd16f`, which is
  how the move is known to have been a move. It is now one fact with two consumers:
  the header above a score, and every cache key. So a prompt edit re-labels the
  number and invalidates every stored answer in the same commit, with nothing to
  remember -- which is #65's second trap, where the first prompt change otherwise
  serves yesterday's answers for ever.

  **`evals/score.py` does not use the cache unless `--cache` is passed**, and the
  environment is erased rather than merely not read: the sanctioned way to run the
  scorer is `set -a; . ./.env; set +a`, which exports a Redis URL that is there for
  the service's benefit. A scored run is meant to be a measurement, and #60's
  evidence was "two identical runs" -- which stops being evidence the moment the
  second one can read the first.

  **A failed call and an unusable answer are never stored; an abstention is.**
  Neither of the first two is something the model said, and caching one would freeze
  a network blip or a schema fault into every future answer for that description,
  ended only by the TTL. `unknown` is an answer, it was paid for, and asking again
  buys the same word at the same price -- and on a real statement the descriptions
  the model cannot place are the largest repeated group there is.

  **Redis being down means "call the model", never "no category"** -- the third
  place in this chain that promise is made, after `AnthropicPredictor` and
  `CategorizerClient`. Every failure is swallowed, counted and logged with its
  traceback. Which makes it, per #64, the third place where an absence has to be
  counted or nobody would ever know: `failures` is kept apart from `misses`
  precisely because both end in a model call and only one is worth an alarm.

  **The measurement that changed the design.** With the container stopped, a lookup
  and then a write each paid the connect timeout in full -- a stopped container
  leaves the SYN unanswered rather than refusing it, which is #39's finding about the
  categorizer arriving one service along -- so a dead Redis added **1055 ms to every
  save**, on the path where a user's transaction is being written. After a failure
  the cache now stops asking for thirty seconds: measured again, **531 ms once and
  then 0 ms per save**. It is deliberately not a circuit breaker library and has no
  half-open state or failure threshold: one failure is enough evidence for something
  whose whole job is to be faster than the alternative. What it costs is that a
  Redis which comes back is unused for up to thirty seconds -- misses, never wrong
  answers.

  **What is measured, against a real Redis with a stubbed model** (a stub because
  the cache is what is being verified, and a stub can be *counted* where a paid call
  cannot): the same description twice is **one** upstream call, one `model_call`
  line and two `cache` lines; a different description and the same description in
  capitals are each a new call; a second process finds the warm answer and makes no
  call at all; 20 hits take 16 ms. Over HTTP against the compose stack with the
  **rules** answering, five requests opened **zero** Redis connections, wrote zero
  keys and logged not one cache line -- #65's third acceptance test, asked of Redis
  rather than of the code. And from inside the compose network the container reaches
  `redis` by service name: 37 ms cold, 0 ms warm.

  **A hit produces no `model_call` line, on purpose.** That line means a call was
  made, so counting them is counting the charges -- which is #65's second acceptance
  test. The cache writes its own line per lookup carrying `outcome=`, what that hit
  did not spend, and the running `hit_rate=`; the totals are in-process and this
  container scales to zero, so the log is the durable record and the last line a
  replica writes is its whole story. Same shape as #64's .NET tally, for the same
  reason.

  **Nothing about the transaction is stored.** The key is a digest and the value is
  an answer, so a dump of this Redis says what was categorised as what and never what
  was bought. #64 made that rule for log lines; this is where it would be far easier
  to break, because storing the description is exactly what would make the entries
  readable by hand.

  **The cost is stored as billed at the time**, not recomputed from today's prices --
  the same argument that keeps the price out of the code in #64, one step along: a
  price change must not rewrite what a past call was charged.

  **Three arguments in `docker-compose.yml` that are decisions rather than tuning.**
  `--save "" --appendonly no`, because a cache whose loss costs one model call should
  not write disk, and no volume for the same reason. `--maxmemory 64mb`, so a runaway
  cannot take the machine. And `--maxmemory-policy allkeys-lru`, because the default
  is `noeviction`, which answers a full cache with an error on **every** SET -- which
  this service would swallow and log for ever while never caching anything again,
  which is precisely the silent failure #65 exists to end. **Nothing `depends_on` it**:
  a Redis that is not up yet is a cache miss, and making the categorizer wait for it
  would turn an optimisation into a start-up dependency.

  **`CATEGORIZER_REDIS_URL` is defaulted on in compose although the everyday loop
  runs the rules**, which is safe by construction rather than by care --
  `cache_from_env` is called from `AnthropicPredictor.from_env` and nowhere else, so
  the rules branch never reads it. Switching one variable to `model` then gets the
  cache with no second edit.

  **`redis>=8.1`, and the major is load-bearing for the fifth time** after #22, #24,
  #39 and #59: from memory this is 5.x, and 8.1.0 is what PyPI answered on the day.
  What lost: `redis[hiredis]`, a native build in an image that has none, to parse one
  short JSON string; the RESP protocol by hand, which is pooling, timeouts and
  reconnection written here instead; and a dict in memory, which loses to the issue
  itself -- this container scales to zero (#61), so a process-local cache is empty
  again about fourteen minutes after anybody stops using it, which is exactly the
  fortnightly spending session this is meant to make cheap.

  **No Redis is deployed, deliberately, and it is a cost line rather than an
  oversight.** The deployed categorizer ran `rules` (#61), and a cache in front of a
  predictor that is not running is a monthly charge for nothing. So flipping that
  variable was three things and not one -- the key as a secret, a cache, and #64's
  price variables -- and `ci.yml` refused a deployment that was `model` with no
  `CATEGORIZER_REDIS_URL`, because that combination is billed per save for ever and
  looks exactly like a working deployment. What the cache would cost, read on
  2026-08-29 and to be re-read rather than trusted: **Azure Cache for Redis Basic C0
  is around 16 USD a month**, the same order as the whole Postgres bill slice 3 faces
  when the free year ends (#34), for a service one person uses weekly. The
  arithmetic to do *before* provisioning anything is whether the per-call bill is
  simply smaller than that -- and #64 already logs the tokens it needs.

  **It was done on 2026-08-30 in #87 and the answer was no cache**, by a factor of
  about thirty: a call is 0.62 US cents, so ~16 USD a month buys ~2,600 calls and
  this application makes 80-160. The gate was refusing the cheaper of the two
  states, so it is gone rather than satisfied, and `ci.yml` now asserts the two
  things that are actually invisible instead -- the key must be a `secretRef` and
  never a literal value, and `model` with no price configured is refused. Nothing
  about #65 is retracted by that: the cache is right locally, where a session
  re-runs the same descriptions, and the paragraph above is what told #87 which
  arithmetic to do. **What would pay is a different cache** -- Anthropic's prompt
  caching over the ~1,150-token constant prefix, which is 97% of the bill, costs no
  monthly charge and is a change to the adapter rather than a resource group.

  **Checked by breaking it, per #21: fourteen mutations, one at a time, reverted
  from a file copy rather than from memory.** All fourteen were caught. Four are
  worth keeping. Folding case in the key is the one this whole feature is shaped
  around and it dies on a parametrised test that exists for nothing else. Leaving
  `decode_responses=True` out of the client is the one that would have shipped
  looking correct -- a cache that is green, correct and never hits -- and it is
  caught by asserting the kwargs the client is built with, because no stub can see
  it. Leaving failures out of the hit-rate denominator reports a healthy 100% for a
  Redis that answered once and was down all day. And caching an answer the adapter
  threw away is the mutation somebody would actually write while tidying, since it
  reads as one more thing worth remembering.

  **CI gains no Redis service container, and that is a property being protected.**
  The tests reach the cache through a stub client the same way #59's reach the SDK,
  so a test that forgot to pass one fails to construct rather than quietly connecting
  to whatever is listening on 6379.

- **Retrieval: the user's own confirmed labels as few-shot examples -- decided
  2026-08-29** (#66). `src/categorizer/src/categorizer/embedding.py` turns a
  description into a vector, `retrieval.py` holds the `ExampleStore` port and two
  implementations, the retrieved rows go into the **user message**, and
  `CATEGORIZER_RETRIEVAL=off|lexical|vector` is the one setting. 56 new Python
  tests, none of which opens a socket. No new dependency.

  **The headline is that the eval set ran out of room before retrieval did, and
  it was arithmetic rather than a surprise.** #60 put the model at 98.9% macro
  recall on the 53 rows -- one miss, an abstention -- so the entire headroom was
  **+1.1 points against a 3-point noise floor**. Section 2 of `docs/evals.md` says
  what that means and it was said before anything was built: this set can detect
  retrieval *harming* the model and is structurally unable to detect it helping.
  `holdout.csv` was labelled for #66 to get a corpus and an eval that do not
  overlap, and it had **less** room: **the model scores 100.0% on it with no
  retrieval at all**. Lexical retrieval holds 100.0%. Section 8 of
  `docs/evals.md` is the account, and its conclusion is that **#47 is now the
  single most valuable open item in the project** -- every future claim that this
  categorizer got better needs rows that are not saturated.

  **What the run actually measured is the prompt, not retrieval**, and it is the
  half worth keeping. `--show-examples` is free and was run first: trigram
  neighbours over this corpus are mostly noise -- `heating` retrieves
  `headphones`, `corner shop` retrieves `cofee` and `t-shirt`, scores around 0.1,
  and the only real hit in ten rows is `minibus` -> `trolleybus`. The model was
  shown five confident, mostly irrelevant labelled rows for nearly every
  transaction and got all ten right. The paragraph telling it the rows were chosen
  by similarity rather than relevance, that some may be irrelevant, and that being
  shown examples is never a reason to stop answering `unknown`, is what absorbed
  that -- and it was written before the run rather than after it. Told nothing, a
  model shown five confident-looking labelled rows reaches for the majority.

  **No score floor was added after seeing those numbers**, and the restraint is
  the decision. Dropping neighbours below a similarity would have tidied the
  output visibly; choosing the threshold by looking at the eval set is exactly
  what `holdout.csv` exists to catch, and `retrieval.py` says so at the one place
  it would go. The single exception is `LexicalStore` discarding rows scoring
  exactly zero, which is not a threshold: no shared trigram at all is not a weak
  match, it is the absence of one, and cosine has no equivalent because it never
  reaches zero between real strings.

  **The examples go in the user message and not the system prompt, and that is the
  load-bearing placement.** `cache.py` keys an answer on the model, the effort, the
  prompt's fingerprint and the user message. Rows in the system prompt would not be
  covered by that key, so the first lookup for a description would be replayed for
  ever -- serving an answer computed from a corpus that has since gained the very
  row that would have changed it. #65's second trap, one issue along, and the same
  shape: an answer remembered under a label that does not describe what produced it.

  **Two prompts, two fingerprints, switched on `bool(neighbours)`.** The examples
  paragraph is appended only when there are examples, which keeps the no-examples
  prompt byte-for-byte the one #60 measured -- `sha256:c8ad9d9fd16f` is unchanged
  and pinned by a test -- so the "off" arm of #66's own comparison did not have to
  be bought again at 53 API calls. The switch is the *presence of neighbours* and
  not "a store is configured": a store that found nothing must produce the base
  prompt, or the call is labelled with instructions about examples that are not
  there, and an empty corpus would orphan every cache entry written since #65 for
  no gain.

  **Anthropic has no embedding model.** Its own documentation says so and points at
  **Voyage AI**, so this is the first time the project depends on anything but
  Anthropic for a model, and the second network call inside the eight seconds a
  save has (#59). `voyage-4-lite`, 1024 dimensions, a 2-second timeout chosen
  against the budget rather than the network. The first **200 million tokens are
  free per account**, and at roughly five tokens a description that is forty
  million transactions -- so the model choice is about latency and not money, and
  256/512 Matryoshka truncations were deliberately not taken for a first
  measurement, because a truncation is a second variable and a number that moved
  for two reasons says nothing about either.

  **The `voyageai` SDK lost, and it is the exact mirror of why the `anthropic` SDK
  won in #59.** There the package bought retries, streaming, error typing and beta
  headers for one dependency. None of that exists here -- one POST, a bearer token,
  a flat JSON body. What it would have cost was measured with `uv pip compile`
  rather than guessed: **51 packages**, among them `langchain-core`, `langsmith`,
  `huggingface-hub`, `numpy`, `pillow` and `tokenizers`, and **three further HTTP
  stacks** (`httpx` 0.28, `requests`, `aiohttp`) beside the `httpx2` #59
  consolidated this service onto. So `pyproject.toml` is unchanged and the call
  goes through the client already in the runtime tree. The day this wants
  reranking, contextualised chunks or the multimodal models, the SDK is the right
  answer and `embedding.py` is what to delete.

  **`input_type` is the parameter that fails silently**, which is why it is a
  `Literal` and not a string. Voyage prepends a different sentence for a query than
  for a document, so identical text embeds differently on purpose; embed a corpus
  as queries and retrieval still returns neighbours, ranked worse, with nothing
  anywhere reporting it. Two call sites choose that word and one test looks at both.

  **The response's order is not promised**, so `embed` sorts by the `index` field
  each row carries -- the "key by id, never by position" rule the Batches API has,
  arriving at a second endpoint. Read positionally, a batch pairs every vector with
  the wrong description, and there is no exception and no log line: retrieval simply
  starts returning unrelated rows.

  **`LexicalStore` is the control and not a fallback.** #66 says out loud that
  whether embeddings beat substring matching on two- and three-word merchant names
  is a real question. A vector store scored against nothing cannot be shown to have
  earned a vendor, a key and a second timeout, so trigram overlap -- padded the way
  `pg_trgm` pads, needing no network -- is scored beside it, and it is free.

  **Nothing in the retrieval path raises.** `neighbours_for` is the one door and
  swallows everything into an empty list, which is the predictor #60 measured at
  98.9%. Third time this project has made that promise, after #39's categorizer and
  #65's Redis -- and per #64 it means the absence must be counted or nobody learns
  about it, so `failed` and `empty` are different words in the log. Per #64's other
  rule the line carries **no description**: the query is the user's own spending and
  so is every neighbour.

  **`CATEGORIZER_RETRIEVAL` refuses an unrecognised value** where the embedding
  timeout and the example count fall back, and the asymmetry is #59's: `vectors` --
  the plural, the typo somebody will actually make -- would serve no retrieval while
  the deployment believed it had some, and the score would be recorded under the
  wrong name.

  **Three refusals in the scorer, each of which would otherwise print a number that
  lies.** `--corpus` may not be `--set`, because every row would retrieve itself at
  a similarity of exactly 1.0 carrying its own gold label and the run would score
  near 100% measuring nothing -- #66's second trap, answered by a refusal rather
  than by a sentence in a README. `--retrieval` with `--predictor rules` is refused,
  because a substring scan reads nothing but the description so a corpus changes
  nothing, and the run would be a with-retrieval measurement of a predictor with no
  retrieval. And a corpus is loaded through the same `load` the eval set uses, so a
  label outside the vocabulary is an error there too -- otherwise the model is shown
  a twelfth category as a worked example by rows it was told to trust.

  **`--show-examples` scores nothing and costs nothing**, which is what makes it get
  used. #66 asks for the chosen examples to be inspectable because a retrieval step
  nobody can look at is untestable; looking at it must not cost one API call per row.
  It reads the store directly rather than reconstructing what the predictor did, and
  prints each row's gold label beside its neighbours -- the fastest way to see the
  failure that matters, five confident neighbours agreeing on the wrong category.

  **What is deliberately not built, and the reason is this file's own rule.** There
  is **no pgvector store and no `psycopg` dependency**, although the issue's title
  names the extension. The measurement above says retrieval has no demonstrated
  value on any data this project holds; the deployed categorizer runs `rules` (#61)
  so nothing there would read it; and it cannot be exercised end to end without a
  Voyage key. Building it now is infrastructure for a feature with no consumer and
  no measured benefit, which is the netshift failure this file exists to prevent.
  What that costs, said out loud: the `<=>` operator, the index and an owner-scoped
  query are the skill #66 was partly for, and they are not gained yet. The
  `ExampleStore` port is the seam they arrive through, and `VectorStore`'s docstring
  names the row count at which an O(n) loop in Python stops being the right answer.

  **Also not built: the live .NET path.** `CategorizeRequest` gains no owner field,
  so nothing sends one -- and it must, before any store is queried in production, or
  one account's descriptions land in another account's prompt. That is the shape of
  the follow-up rather than a detail of it.

  **The vector arm is implemented, tested and unrun.** It needs `VOYAGE_API_KEY`,
  which is the owner's act the way #76 was for the Anthropic key, and the free tier
  means it costs nothing but the signing up. `transactions.csv` was not re-scored
  with retrieval either: 53 calls to move a number by at most 1.1 points is the
  reading to buy **after** #47, not before.

- **The suggestion while typing: `POST /api/transactions/category-suggestion`, a
  400 ms debounce, and a badge under the description field -- decided 2026-08-29**
  (#67). `src/LandMoney.Web/Categorizing/CategorizerKind.cs` names what asked;
  `src/landmoney.client/src/hooks/useCategorySuggestion.ts` is the client half.
  34 new tests, and they still need no Postgres, no Docker and no network.

  **What it fixes is that the one visibly intelligent thing in this application was
  invisible.** The category arrived after the save, in a table row, with nothing
  saying anything had thought about it.

  **A POST that writes nothing, and the method is the decision.** A GET reads
  better for a question -- idempotent, cacheable, and "does not write anything" is
  what it means -- and it loses twice. The description would travel in a query
  string and into every access log between the browser and the process, which is
  #64's rule about keeping the user's spending out of a log, arriving at a URL. And
  a GET is a top-level navigation, so the `SameSite=Lax` cookie goes with it: #52
  records two CSRF locks and a JSON POST keeps both where a GET keeps neither.

  **The browser may not call the categorizer**, which is what forces an endpoint
  here at all: it is internal-only with no public ingress (#61), and it is
  unauthenticated, so anything that could reach it directly would be somebody
  else's Anthropic bill.

  **`CategorizerClient` had to learn a distinction the save path never needed, and
  it is the substance of the change.** An abstention and a dead service are both
  null there, correctly -- neither stores a category. Here they are two different
  screens: "no idea" is a normal answer on roughly a third of the labelled set and
  has to be *visible*, while a categorizer that is not running has to be
  *invisible*, because there is nothing the person typing could do about it. On the
  wire from the Python service both are one `null`, so `CategorizerAnswer` carries
  who answered beside what they said, and **the source is what says something
  answered at all**. That is why the source guard moved above the abstention: on a
  path FastAPI cannot produce -- a 200 with neither field -- the outcome is now
  `unusable` rather than `abstained`, which is the more truthful of the two.

  An answer this side refuses (a category longer than the column) reports as
  *nothing* rather than as an abstention, although the source is right there. It
  had an idea; this side will not use it, and "rules had no idea" would be this
  application putting words in another process's mouth.

  **The save asks again, and does not take the answer from the browser.** So the
  screen and the row are two calls. What lost is one call and a guarantee they
  agree -- and it lost on provenance: a client that can send a category can send a
  source, and a row claiming `model` because a browser said so is exactly the hole
  `transactions.category_source` was added in #59 to close. `UpdateCategoryRequest`
  already makes the same call in the same words. What makes the two answers agree
  in practice is that the deployed predictor is deterministic (#61) and the model's
  is keyed on these three fields in #65's cache -- which is why the endpoint
  uppercases the currency before asking, exactly as `CreateAsync` does. A preview
  sending `eur` would miss the entry the save writes under `EUR`, pay twice, and be
  free to answer differently.

  **The calls are now counted by what asked for them**, which is #64 being kept
  honest rather than an addition to it. From here on the previews are the majority
  and against the model each one is a charge, so "12 recorded" without the split
  would read as twelve transactions. `kind` is a dimension and not a second set of
  counters: a preview fails in the same nine ways a save does. What that
  deliberately does not do is split the *outcomes* by kind -- "did saves get
  categories" is a query over the per-call lines, which carry both words, and the
  number that could not be recovered from anywhere else is in the summary. Every
  kind is printed including a zero, unlike `BySource`, because there are exactly
  two and both are this application's: `preview=0` says the screen asked for
  nothing, which after this shipped is a symptom.

  **`CategorySuggestionRequest` copies `CreateTransactionRequest`'s rules and has
  no date.** The date is absent because a day tells a predictor nothing and a field
  an endpoint does not read is a field a caller can be refused for getting wrong --
  a mistyped year would otherwise stop the suggestion appearing for a reason
  unrelated to the description. The rules are copied because they exist here for a
  different reason: not to protect a column, but to keep the outbound request
  inside what `CategorizeRequest` in `contracts.py` accepts, since an amount waved
  through here comes back as a 422 that reads like the categorizer misbehaving.
  Shared constants were the alternative and remove the literal duplication without
  removing the risk, which is a rule added to one and forgotten on the other;
  `CategorySuggestionRequestTests` reads both types by reflection and fails naming
  them, which is the answer `CategoriesTests` already gives to the same problem.

  **On the client: no `asking` state and no `failed` state**, and both absences are
  decisions. A failure shows exactly what a suggestion nobody asked for shows,
  which is nothing -- #67's third acceptance test, and the same promise
  `CategorizerClient` makes on the server. A "thinking..." line would flash for
  four milliseconds against the deployed rules and would sit on screen for the
  whole timeout against a categorizer that is not there, then vanish: an indicator
  asking the reader to wait for something they are never going to be given.

  **The out-of-order response is answered by the effect's cleanup**, which aborts
  the request the previous keystroke started, and by an `aborted` check before the
  `setState` -- the one gap the abort itself does not close, where the response has
  already resolved. StrictMode's double-run is the free test of it and costs no
  request at all, because the first run's debounce timer is cleared before it
  fires. The dependencies are the three values and never an object built from them:
  a literal is a new reference every render, and the effect would fire on every
  keystroke anywhere in the form.

  **The previous suggestion stays visible while a newer one is on the way**, which
  is the one thing here that is deliberately a beat out of date. Clearing per
  keystroke flickers, and nothing is stored from this path -- the answer that
  decides the row is the server's at save time.

  **5 seconds, not the client's usual 10.** That constant is a backstop against a
  hang; this is a deadline on an answer whose whole value is that it arrives while
  the description is still on screen. What it costs, said out loud: the first
  request after an idle spell pays a cold start (23.3 s for the app, #35, and the
  categorizer scales to zero too, #61), so it times out and the field shows
  nothing.

  **This is the first endpoint in the application that can be tested end to end**,
  because it touches no database -- routing, the authorization group, binding,
  `ValidationFilter`, the handler and `CategorizerClient` are asserted against
  bytes. Two seams are stubbed for opposite reasons: the categorizer to control
  what it answers, and authentication because the alternative is `UserManager`,
  which is Postgres, which is the property #22 defends. `TestApp` gained a
  `With(services)` hook for it. What that does not check is that a real cookie is
  accepted, the client's debounce and aborts, and a preview agreeing with the save.

  **Checked by breaking it, per #21: seven mutations, one at a time, reverted with
  `git checkout` from the commit rather than from memory.** All seven were caught.
  Two are worth keeping: dropping `ToUpperInvariant` in the handler, which is
  invisible against the rules and silently doubles the model's bill; and reporting
  an unusable answer as an abstention, which is the tidy-looking version of putting
  words in another process's mouth. **No mutation was run against the React half,
  because there is nothing to run it with** -- this client has no test framework,
  so the debounce, the abort and the three rendered states are checked by reading
  and by hand.

  **`Lidl` is not a rule, and #67's own acceptance test says to type it.** Measured
  against the running compose stack: `{"category":null,"source":"rules"}`. So the
  first acceptance test as written shows *"No suggestion -- rules"*, which is the
  second acceptance test rather than the first; `coffee at the cafe` is the
  description that demonstrates a suggestion. #61 recorded this exact trap about
  its own acceptance test and it was written into the next issue anyway, which is
  worth more as a note about how these are read than about the categorizer.

  **Nothing rate-limits the endpoint**, and the only thing between this screen and
  an unbounded number of model calls is that a person types slowly. Acceptable
  while registration needs an invite code and the deployed categorizer runs the
  rules (#61); it is the first thing to revisit when either stops being true.

- **The month at a glance: totals by category, in the client, from the rows that
  are already there -- decided 2026-08-29** (#68).
  `src/landmoney.client/src/summary.ts` does the adding,
  `src/landmoney.client/src/money.ts` holds the one conversion that makes it
  legal, and `components/MonthSummary.tsx` is the screen. **No .NET code changed
  at all**, and no new dependency.

  **What it fixes: the application categorised spending and then never used a
  category for anything.** A list sorted by date answers "what did I buy"; it
  does not answer "where does the money go", which is the only reason the
  categorizer exists. Five issues of AI work were visible in exactly one table
  column.

  **Nothing was summed on the server, and that is the decision the shape hangs
  off.** #68 allows either -- "sum on the server in `decimal`, or sum in minor
  units on the client" -- and names the client route's own cost in the same
  breath. What decided it is that `GET /api/transactions` is fetched whole (#3,
  no paging, no limit), so the client already holds every row: the summary is one
  pass over the array the table below it is about to render. **The totals and the
  rows are therefore incapable of disagreeing**, which makes #68's first
  acceptance test true by construction rather than by checking it once. A
  `GROUP BY` endpoint would have bought a second round trip, a third contract to
  keep in step with `api/types.ts` by hand, and a window in which the two halves
  of the screen were fetched at different moments.

  **What it costs is the trap the issue names, and it is written into the
  component rather than solved:** it stops being fine *silently*. The day
  `/api/transactions` grows a page, this screen keeps rendering and starts
  describing the page rather than the month -- no error, no warning, a plausible
  number. The fix that day is the server-side sum, not a bigger page.

  **The arithmetic is in integer minor units, and what that is worth was measured
  rather than asserted.** `toMinorUnits` is the only conversion in the client and
  every addition goes through it, because `api/types.ts` promises the amount
  round-trips exactly *as long as nothing does arithmetic with it*. The honest
  finding is smaller than the rule sounds: two million two-decimal amounts summing
  to about a billion drift by **2.9e-6** as doubles, and the double total and the
  exact total render to the same two-decimal string at every point along the way
  -- because every input is already a two-decimal value, so the exact sum never
  sits on a rounding boundary a millionth could push it over. So this is a
  coincidence being turned into a property, not a bug being fixed. Worth saying
  plainly, because the opposite claim was the first thing written in that comment
  and it was wrong.

  **Currencies are the outer grouping and not a column**, which is #68's first
  trap answered by the type rather than by care: every total in `CurrencyTotals`
  lives inside a `currency`, so there is nowhere on the screen a number mixing EUR
  and MDL could go. A flat list of `{currency, category, total}` rows carries the
  same facts and leaves that addition one `reduce` away. **The currency blocks are
  ordered by transaction count and deliberately not by their totals** -- ranking
  500 MDL above 400 EUR is the same mistake as adding them, and a count is a count
  in any currency.

  **The month is a string prefix, never a parsed date.** `occurredAt` is already
  "2026-08-19" and `new Date("2026-08-19")` is UTC midnight, so west of UTC every
  row on the 1st would be counted in the previous month -- #17's day boundary
  arriving in a filter. The current month comes off the **local** clock, which is
  the calendar the reader is looking at, and is read during render rather than
  frozen in state so a tab left open across midnight on the 31st is one reload
  away from being right.

  **A category with no spending this month is absent rather than zero**, and that
  falls out of building the rows from the transactions instead of from
  `Categories.All`. Starting from the eleven would have to remember to drop the
  empty ones, and would show eleven rows in a month with three purchases in it.

  **`formatAmount` moved out of `TransactionList` into `money.ts`.** Not tidying:
  `minimumFractionDigits: 2` is a decision about this application's column rather
  than about the currency -- the yen has no minor unit and an amount stored as
  12.34 would *display* as 12 -- and a second copy of that would have agreed by
  luck.

  **An empty month renders as an empty month; an empty account does not.** #68
  asks for the first, and somebody who has spent nothing since the 1st should see
  that said. The second is not a month problem, and rendering it would stack
  "Nothing recorded this month." on top of the list's "Nothing recorded yet" --
  the same fact twice, and only one of them says what to do about it.

  **Nothing here is covered by a test, and that is the honest status rather than
  an omission.** This client still has no test framework, which #67 recorded for
  its own debounce; the .NET suite is untouched at 260 green because no .NET code
  changed. `summariseMonth` and `monthOf` were pulled into `summary.ts` -- out of
  the component file, which the fast-refresh lint rule objected to and which is
  the smaller reason -- so that the pure half is one dependency away from being
  testable rather than a refactor away.

  **Verified against the running stack, and the one gap in that is named.** Ten
  transactions were posted through the real API on a fresh account: seven EUR and
  two MDL in August, one EUR in July. The screen reports EUR 7 transactions
  totalling 299.83 -- uncategorised 129.99, groceries 123.80, eating-out 46.03,
  transport 0.01 -- and MDL 2 transactions totalling 290.00, with July's 999.99
  absent from both. Each figure was added by hand off the list below it. What was
  **not** done is opening the signed-in page in a browser, because signing in
  means typing a password: the components were mounted against the rows the real
  API returned, in the order `App.tsx` renders them, which checks everything
  except that a real session reaches them.

  **The caption was reworded after looking at it.** Intl renders a currency with
  no symbol as its code, so "MDL -- MDL 290.00 in total" was the first version and
  reads like a bug, while "EUR -- €299.83" has the same shape and hides it. The
  count now sits between them.

- **The model in production: `CATEGORIZER_PREDICTOR=model`, the key as a Container
  Apps secret, and no cache -- decided 2026-08-30** (#87). The commands are the new
  *Turning the model on* subsection of step 16 of `docs/deploy-azure.md`; the
  repository half is `ci.yml`'s `Check the categorizer` step, which lost #65's Redis
  gate and gained two others. **No application code changed at all**, in either
  language, and no new dependency.

  **What it fixes is that the repository held a number it did not use.**
  `docs/evals.md` section 7 recorded 98.9% macro recall against the baseline's
  56.1%, measured twice with zero confident errors -- and every transaction saved
  through the site was still categorised by 109 substrings, because #61 pinned the
  deployed categorizer to `rules` and said out loud that turning it over was a
  decision with a bill rather than a configuration change.

  **A call is 0.62 US cents**, measured on 2026-08-30 through `evals/score.py` with
  #64's price variables set, at a total cost of about three US cents for the
  exercise: **1,173 input tokens and 11-13 output tokens**, ~2.1 s, `claude-opus-5`
  at `effort=low`. Two things in that contradict what the code assumes about
  itself, and both matter more than the total.

  **Output is eleven tokens, so the answer is 2.5% of the bill and the prompt is
  the other 97.5%.** Adaptive thinking at `effort=low` writes essentially nothing
  on a one-word classification against a rubric supplied in full, so the
  `max_tokens=2048` headroom `anthropic_predictor.py` reserves for thinking is
  never touched -- its comment is right about why it is there and wrong about it
  costing anything. `CATEGORIZER_EFFORT` is a latency and quality lever and **is
  not a cost lever**; the only cost lever is the prompt. And **input is ~1,173
  where `prompt.py`'s text measures ~700**: the other ~450 is `RESPONSE_SCHEMA` and
  the message framing, so anything that prices this by measuring the file is low by
  60%.

  **A saved transaction is not one call, which is #67 arriving in the bill.** The
  categorizer is asked once 400 ms after the typing stops and again when the row is
  saved -- the save deliberately does not trust the browser -- and every
  `(description, amount, currency)` surviving the debounce is its own call. Two to
  four calls a transaction, 1.2 to 2.5 cents, so **50 cents to a dollar a month**
  at forty transactions. A month of CSV imports costs nothing: #62 never calls it.

  **No Redis, and this is the decision the issue turned on.** #65 wrote a gate into
  `ci.yml` refusing `model` with no `CATEGORIZER_REDIS_URL`, and wrote in the same
  breath that the honest third option was no cache at all and that **the arithmetic
  was the thing to do before provisioning anything**. Done: ~16 USD a month buys
  ~2,600 calls, this application makes 80-160, so the gate was refusing the cheaper
  of two states by a factor of about thirty. It is **gone rather than satisfied**.
  Nothing about #65 is retracted -- the cache is right locally, where a session
  re-runs the same descriptions, and it is the paragraph that told #87 which sum to
  do. What is given up is real and small: preview and save are two calls where one
  would do, so a third of the bill is duplicate, and a third of a dollar is not
  worth sixteen.

  **The cache that would pay is a different one and is deliberately not in this
  change.** Anthropic's prompt caching over the ~1,150-token constant prefix --
  system prompt plus schema, byte-identical every call -- is where 97% of the bill
  is, needs no resource and no monthly charge, and `_user_message`'s docstring
  already keeps the varying half at the end for exactly that day. It is a change to
  the adapter, so it is its own issue.

  **What replaced the gate is the two states that really are invisible.**
  `ANTHROPIC_API_KEY` must arrive as a `secretRef` and never as a literal value --
  asserted for *both* predictors, because `--set-env-vars "ANTHROPIC_API_KEY=..."`
  deploys and answers correctly while leaving the key readable to anyone who can
  run `az containerapp show`, and leaving it in that revision's template for as
  long as the revision is listed, which outlives rotating it. And `model` with no
  price configured is refused, because #64 kept the price out of the code so a
  stale figure could not be believed, and the consequence is a deployment that
  bills while every line it writes says `cost_usd=unpriced`.

  **The leak check counts rather than fetches**, which is the one line in it worth
  copying elsewhere: `length()` over a JMESPath filter answers `0` or `1`, where
  reading `.value` and testing it for emptiness would pull the key into a runner
  variable on exactly the run that is reporting the key exposed -- one `set -x` or
  one later `echo` from publishing it in a public repository's build log. The
  filter excludes the empty string as well as null, because a variable filled from
  a secret comes back with `value` present and **empty** (step 12's acceptance
  test), so testing only for null would call every correct deployment a leak.
  Verified against all four shapes -- unset, secretRef with an empty value,
  secretRef with no value at all, and a literal -- and against the live app, which
  answers 0.

  **`model` with no key secret is refused too, and that turns #87's first trap into
  a red step.** `anthropic.Anthropic()` constructs cleanly with no credential and
  defers the failure to the first request, so a deployment that selects the model
  and forgets the key **starts, serves 200s, and answers `category: null` for
  ever** -- which on the .NET side is an *abstention*, counted and not logged (#64),
  and indistinguishable from a model declining every row. The issue's own advice was
  to read the log after the first deploy; a check that cannot be forgotten is worth
  more than advice that can.

  **The ceiling is set at Anthropic and not here, and nothing in the deployment
  caps spend.** `--max-replicas 1` bounds concurrency, not money: `/categorize` is a
  `def` handler so Starlette dispatches it to a forty-thread pool, which at 2.1 s a
  call is ~19 calls a second, **~7 USD a minute**. Nothing rate-limits the preview
  endpoint either -- #67 said so, and the only thing between that screen and an
  unbounded number of calls is that a person types slowly. So the control is a
  **monthly spend limit on the workspace the key belongs to**, which is the owner's
  act, costs nothing, and is the only control here that a bug in this repository
  cannot defeat. What makes it the right shape rather than merely the available
  one: **it degrades into the state the application already handles** -- a refused
  call raises, is caught, is logged as `outcome=failed`, and becomes a null
  category. #39's fallback, unchanged by a model being behind the port. A spend
  limit does not take the site down.

  **The acceptance test is `haircut` and not `Uber ride to the airport`**, and both
  halves were checked before being written down -- which is the mistake #61 and #67
  each made once, writing an acceptance test whose input produces the failure it
  exists to detect. The rules answer `unknown`; the model answers `other`, which is
  the category `docs/evals.md` records as structurally unreachable by substring
  matching and which the model took from 0/3 to 3/3. A description both predictors
  answer identically would show a category and prove nothing about which one
  produced it.

  **What Claude did not do, and it is the whole Azure half.** The key is a
  credential and the three commands spend money, so creating the secret, flipping
  the variable and setting the spend limit are the owner's acts -- the same
  division as the app registration in step 14 and the first account in step 15. The
  repository is ready and CI will refuse the wrong shapes of it; nothing is
  deployed by this change. `az containerapp show` reported
  `CATEGORIZER_PREDICTOR=rules`, no secrets and no key on 2026-08-30, which is what
  the new checks were exercised against.

- **The Data Protection key ring: a blob, wrapped with a Key Vault key, read with
  the app's own managed identity -- decided 2026-08-30** (#88).
  `src/LandMoney.Web/Auth/DataProtectionSetup.cs` is the whole of it; the commands
  are a new subsection of step 15 of `docs/deploy-azure.md`; `ci.yml` gained a
  *Check the key ring* step. Three packages, and 26 new tests that still need no
  Postgres, no Docker and no network.

  **This is the item #52's own list named as most likely to be worth fixing next,
  and it is the only one that cost the owner something every single day.** The
  authentication cookie was encrypted with keys generated in memory, which die
  with the process -- and with `--min-replicas 0` that is roughly every fourteen
  idle minutes (#35). So coming back to the site after a pause meant **typing a
  password**, where under the OpenID Connect version that lived for one day a live
  provider session made it a redirect and no typing.

  **Three packages, not the two #88 names.**
  `Azure.Extensions.AspNetCore.DataProtection.Blobs` and `.Keys` bring
  `Azure.Storage.Blobs` and `Azure.Security.KeyVault.Keys` between them and
  **neither brings `Azure.Identity`** -- read off the nuspecs rather than assumed,
  because a missing credential package fails at the `DefaultAzureCredential` line
  rather than at restore, which reads like a using directive. Blobs 1.5.4, Keys
  1.6.4, Azure.Identity 1.21.0, all checked on the day, which is the sixth time
  that rule has paid after #22, #24, #39, #59 and #65.

  **What it costs, off the Azure retail prices API for `polandcentral` on
  2026-08-30 rather than off a blog:** blob Hot LRS is 0.0196 USD per GB-month
  with reads at 0.0043 and writes at 0.054 per 10K; Key Vault Standard operations
  are 0.03 USD per 10K. **Neither resource has a monthly base charge.** The ring is
  one XML file of a few kilobytes, read once per process start and written once
  every ninety days when a key rolls, so the bill is **a fraction of one US cent a
  month** -- four orders of magnitude under the 15-20 USD Postgres faces when the
  free year ends (#34). #88's first trap is answered in the shape it asks for:
  small is not free, and the arithmetic was the cheap part. What is actually spent
  is two more resources to keep track of. (RSA 2048 rather than 3072 or an elliptic
  curve, which Key Vault bills as *Advanced Key Operations* at five times the rate
  -- meaningless in money here, and nothing in a wrap benefits from more.)

  **`VerifyKeyRing` is the half that is not two package references, and it exists
  because of a measurement rather than a suspicion.** `XmlKeyManager` hands each
  stored key to `DefaultKeyResolver`, which asks whether it can produce an
  encryptor -- a question that goes through Key Vault, because the descriptor is
  encrypted with it. `DefaultKeyResolver` **catches every exception that throws**,
  logs it at Warning, and marks the key ineligible; with no eligible key left,
  `KeyRingProvider` does what it does on a brand-new installation and generates
  one. Measured against the real framework with a file-system store and
  certificate protection standing in for the two Azure resources -- same shape, no
  network -- by writing the ring with one certificate and re-opening it with
  another:

      3b. Unprotect threw CryptographicException: Unable to retrieve the decryption key.
      3c. Protect SUCCEEDED -- so a new key ring was generated over the unreadable one
      3d. keys on disk now: 2

  So **the framework's answer to a key it cannot read is to replace it**: a working
  site, a warning nobody reads, and everybody signed out. That is #88's own bug
  arriving through the fix for it, which is why reading the whole ring once at
  startup and refusing on any key that will not decrypt is not optional hardening.
  Note what the same run says about `GetAllKeys()`: it does **not** throw, because
  `Key.Descriptor` is resolved lazily. Only asking a key to do work surfaces it.

  What that costs, said out loud: with `--min-replicas 0` this runs on every cold
  start, so an Azure blip during a wake-up is a replica that fails to start rather
  than one that serves. That is what #88 asks for in as many words, and the
  alternative is the failure that looks exactly like success. Measured on this
  machine against a storage account that does not exist: the process refuses to
  start and names the host, after **52.7 seconds** -- six SDK retries against a DNS
  failure. Loud, and slow enough that the request which woke the container gives up
  first. The retry count is deliberately left at the default: a 503 from storage is
  worth retrying and the SDK cannot tell it from an NXDOMAIN.

  **Absent is legal, half configured is not, and the asymmetry is the design.**
  Neither key set is the state a developer machine and `efbundle` are both in, and
  it has to stay legal for both -- #57 is what a required-configuration throw on
  the bundle's path costs, and it was re-checked rather than assumed by building a
  bundle and running it in an empty directory, where it still answers "No such host
  is known." rather than "Unable to create a 'DbContext'". So the unconfigured
  deployed case is an **error in the log plus an assertion in `ci.yml`**, which is
  the same answer #61 gave for `Categorizer__BaseUrl` and for the same reason.

  **What that same run settled, and it is general rather than about #88:
  `efbundle` executes nothing below `builder.Build()`.** Run with both key ring
  variables pointing at resources that do not exist, the bundle logged the
  registration line -- which is above Build -- and then failed at **Postgres**,
  never at the blob, although `VerifyKeyRing` sits between the two and would have
  thrown first. The host factory resolver stops the program at Build to take the
  `DbContext`. The half of that worth carrying forward is the cost: `ci.yml`'s
  "The bundle must start without appsettings.json", which is the guard #57 bought,
  therefore covers the registrations and **nothing after them**.

  One of the two alone **throws at startup**, and that is safe against #57 precisely
  because the bundle has *neither*: reaching that branch means somebody set one,
  which is a mistake to report rather than a state to tolerate. The two halves are
  not symmetrical and the dangerous one is the half that works: a vault key with no
  blob is nonsense and fails at once, while **a blob with no vault starts, persists,
  keeps everybody signed in, and leaves the key that decrypts every session cookie
  in a container as plain XML** -- a downgrade nothing would report.

  **A leading slash is an absolute URI on Linux and not on Windows, and that cost
  a red CI run to find.** The two values were checked with
  `Uri.TryCreate(..., UriKind.Absolute, ...)`, and the theory case
  `/keys/dataprotection` was green on this machine and red on the runner:

      Assert.Throws() Failure: No exception was thrown
      Expected: typeof(System.InvalidOperationException)

  On Unix a leading slash is an absolute *file* path, so `TryCreate` answers true
  and hands back `file:///keys/dataprotection`. **The deployed container is Linux**,
  so the weaker check would have accepted a path in the one environment that
  matters and refused it on the machine it was written on -- green build, and a
  failure at the first wrap with a message about a file. The check is now
  `Scheme == Uri.UriSchemeHttps`, which is the tightest thing still true of both a
  blob endpoint and a Key Vault key identifier, and which makes a bare container
  name, a relative path and an accidental `file://` all fail in one place. http is
  refused with them, although it parses: neither endpoint speaks it, so accepting
  one would only move the failure later.

  Worth keeping as the general form rather than as this instance: **a `Uri` check is
  a platform-dependent check**, and this repository writes on Windows and runs on
  Linux. It is the same shape as #57's green-build / red-deploy, one API along.

  **`SetApplicationName("LandMoney")`, written out, and its absence is a silent
  sign-out.** The default application discriminator is derived from the content
  root path and mixed into every purpose string, so two processes sharing a ring
  and disagreeing about where they run cannot read each other's cookies. In this
  image the path is `/app` and always has been, which is exactly what makes the
  default look safe -- it holds until something changes the working directory, and
  then it fails as "everybody signed out" with the key ring intact and blameless.
  It is pinned by a test that reads `DataProtectionOptions.ApplicationDiscriminator`
  off a bare `ServiceCollection`, which needs no network.

  **`DefaultAzureCredential`, not `ManagedIdentityCredential`.** In the container
  the two are identical -- Container Apps sets the identity endpoint variables, so
  the chain finds managed identity without probing IMDS and hanging. The difference
  is on a developer machine, where the chain picks up `az login` and the precise
  one refuses; this is the only configuration in the application that cannot be
  exercised locally at all, so the one route to debugging it is worth keeping open.
  What it costs is the trap of that class: the chain is ordered and silent about
  which link answered, so a stale `AZURE_CLIENT_ID` or an `az login` against the
  wrong tenant authenticates as somebody else and the error is a 403 about a role
  assignment that is in fact correct.

  **The two identities are different, and #88 says so before anyone discovers it.**
  The OIDC federation of #38 belongs to the **workflow** -- it is what `azure/login`
  trades a GitHub token for. The **running container** is a separate principal, and
  it is the one both role assignments are on. `ci.yml` asserts the app has a
  system-assigned identity for that reason: losing it is one `containerapp identity
  remove` away and produces a 403 at the next cold start, with nothing in the
  deployment that caused it saying so.

  **Three things in the role assignments that are decisions rather than syntax**,
  all in step 15. `--assignee-object-id` with `--assignee-principal-type`, never
  `--assignee`, because the friendly form does a Graph lookup and a managed
  identity created seconds earlier has not replicated -- the failure is `Cannot find
  user or service principal in graph database`, which reads as if the identity was
  not created. Both scopes reach *past* the resource to the container and to the
  key, rather than granting every container the account will ever have. And
  `--allow-shared-key-access false` on the storage account kills the account key
  outright, which is the point and which means the **owner** needs a data-plane
  role too: holding Owner or Contributor grants nothing over blobs.

  **The Key Vault key URI carries no version, and that is load-bearing.** Pinning
  one works until the key is rotated, at which point the application keeps asking
  for a version that is no longer current. Versionless, the wrap uses whatever is
  current and the unwrap uses the version recorded inside the ring, so rotation
  costs nothing and old keys stay readable.

  **The check is `ci.yml`'s last step, and that is #61's one-time-bootstrap shape
  reused.** The runbook creates resources and CI asserts them, so the first run
  after this merges is red there and nowhere else if step 15 has not been run --
  after everything before it has gone green, with nothing left half applied.

  **What is deliberately untested, said plainly:** that
  `PersistKeysToAzureBlobStorage` and `ProtectKeysWithAzureKeyVault` do what they
  say. Both need a storage account, a vault, an identity and a network, which is
  the same wall `AuthorizationTests` and #62 both document. #88's three acceptance
  tests -- a session surviving a new revision, two replicas accepting each other's
  cookies, and a vault role removed producing a refusal rather than a fresh ring --
  are by hand, and step 15 has the commands.

  **Checked by breaking it, per #21: twelve mutations, one at a time, reverted with
  `git checkout` from the commit rather than from memory.** Eleven applied and all
  eleven were caught; the twelfth did not compile, the compiler refusing to let the
  ephemeral branch fall through to the persisted one. Two are worth keeping.
  Replacing `IsNullOrWhiteSpace` with a null check failed **26** tests rather than
  one, because `appsettings.json` now ships both keys present and empty -- so the
  committed empty strings turn a plausible null-check slip into a failure
  everywhere instead of a half-configured throw in one deployment. And the
  substitution script refuses to proceed when a pattern matches zero or two places,
  which is #21's own lost mutation written into the tool: the sweep there silently
  changed a comment instead of the call beneath it.

  **Still deliberately open:** nothing rotates the Key Vault key, and nothing
  prunes revoked or expired entries from the ring. Both are Key Vault's own
  rotation policy and a `RevokeKey` call respectively, neither has a consumer yet,
  and the ring gains one entry every ninety days.

- **The way out of the database: `GET /api/transactions/labelled`, five columns,
  `category_source = human` only -- decided 2026-08-31** (#89).
  `src/LandMoney.Web/Export/` holds the writer, the rendering and the one
  predicate; the screen is
  `src/landmoney.client/src/components/ExportLabelled.tsx`. 50 new tests, and
  they still need no Postgres, no Docker and no network. No new dependency, and
  no migration.

  **What it fixes is an asymmetry rather than a fault.** #63 made every
  correction a labelled row produced by the one person who can judge it, during
  ordinary use -- and those rows went into Postgres and stayed there, because
  `evals/score.py` reads a CSV and there was nothing that wrote one. The roadmap
  said so in as many words. Three issues have now each removed an excuse -- #62
  the typing, #63 the labelling, #89 the getting-out -- and none of them can tick
  the box they all point at, because what that asks for is data and every one of
  them is a route.

  **Where the export lives was the decision, and the alternative was a `psql`
  query written into `docs/`.** That is genuinely less code -- no route, no
  writer, no screen -- and #35 has already established that this machine reaches
  the deployed database. It lost on #89's second trap. The export has to be
  scoped to one owner, and in `psql` that scoping is a `WHERE` clause somebody
  types, against an owner id they first have to look up; here it is
  `AppDbContext`'s global query filter, applied to a query that does not mention
  ownership and therefore cannot forget to. **The failure modes are not
  symmetrical**: a forgotten clause in a hand-typed query exports every account's
  rows into a file that looks exactly right, and #52's bug is this repository's
  own evidence that that class of mistake is invisible from outside. Two smaller
  things went the same way -- `psql` is a dependency #37 already declined for the
  same "another thing to install, another place the connection string arrives"
  reason, and the labelling being exported is done in a browser, so a route that
  needs a terminal is a different act from the one that produced the rows.

  What the route taken costs, said plainly: about ninety lines and a card on the
  screen, for something one person runs a handful of times a year. That is the
  honest size of it, and it is why this was argued rather than assumed.

  **A GET, which is #67's decision going the other way and for the reason #67
  gave.** That endpoint is a POST because a description in a query string is one
  person's spending written into every access log between the browser and the
  process. Nothing about this request is a value -- there is no query string at
  all -- so the method means what it says: a read, idempotent, safe to repeat.

  **The header is included, so the export is a valid eval set on its own**, and
  `python evals/score.py --set <file>` reads it -- which is worth doing before
  merging, because it says what the baseline makes of rows nobody wrote for it.
  The price is that appending it is `tail -n +2` rather than `cat`, and the
  screen prints both commands. Omitting the header was the alternative and buys a
  file that concatenates directly and cannot be read by the one program it exists
  to feed.

  **Nothing is downloaded when nothing was labelled**, and that is a decision
  rather than a guard against an empty string: the body is still a valid file --
  a header and no rows -- and it is precisely the file that does damage, because
  appended it puts a second header into the middle of the set, which `score.py`
  refuses as a row whose date is the word `occurred_at`.

  **`X-Labelled-Rows` is a header rather than a field in a JSON envelope**,
  because the body has to stay a file: anything wrapping it puts the whole export
  through JSON escaping and makes `curl -OJ` produce something that is not the
  CSV. Counting the lines in the client is the alternative and is wrong on
  exactly the rows worth having -- a quoted description may contain a newline, so
  lines and rows are not the same number, and the browser would need a second CSV
  parser to find out.

  **The one `WHERE` clause is a named rule, not a lambda**, and it was a lambda
  until a mutation sweep said otherwise. `LabelledRows.ByHand` is the precedent
  `CategorySources.MayOverwrite` set in #63 -- a rule that is easy to lose, in a
  place where losing it is silent. An `Expression` and not a `Func`, or EF fetches
  the table and filters it in memory. What a test still cannot hold is that the
  handler *applies* it: that is a one-line deletion in a method that reaches
  `AppDbContext`, which is the wall `AuthorizationTests` and #62 both document,
  and it was checked by hand instead.

  **The export is ordered oldest first, which is the opposite of the screen.**
  `evals/transactions.csv` is in date order and this file is appended to it, so
  ascending keeps it sorted where newest-first would interleave backwards.
  `CreatedAt` is the tiebreak for the same reason `ListAsync` has one -- a day
  holds several rows -- and with it two exports of an unchanged table are
  byte-identical, which is what makes a diff of two of them mean anything.

  **CRLF, and no BOM.** RFC 4180 says CRLF, which is the weaker half; the
  stronger is that `evals/transactions.csv` is CRLF in the working tree --
  `core.autocrlf` is true and git stores it as LF -- so an appended block of LF
  rows would be a change to every line of the file the next time anything
  rewrote it. The BOM is left off because `score.py` opens the set as `utf-8-sig`
  and would read it, and because `TypedResults.Text` with an explicit encoding
  writes the encoded bytes without the preamble.

  **Two files, two cards, and never the same name.** #89's third trap is that
  the file `POST /api/transactions/import` reads has four columns and no
  category and this one has five, so naming them alike is how they get swapped.
  The download is `labelled-<date>.csv`, `CsvWriter` lives apart from
  `CsvReader` although both are RFC 4180, and the screen is a second card beside
  the import rather than a second control inside it -- adjacent, so the
  difference is visible at the same moment, and separate, because one card
  holding an upload and a download is where somebody eventually feeds one to the
  other. A test asserts the export's name does **not** contain "transactions".

  **The header is a contract with a Python file, so the Python file is read.**
  `score.py` compares its `COLUMNS` tuple against the header exactly rather than
  by lookup, so a column renamed on either side makes every export unreadable by
  the one program meant to read it, with nothing red anywhere because each side
  stays self-consistent. `LabelledCsvTests` reads `evals/score.py` by
  `[CallerFilePath]`, which is the answer `CategoriesTests` already gives for the
  vocabulary, with the same cost: the test knows the repository's layout.

  **Checked by breaking it, per #21: 21 mutations, one at a time, reverted with
  `git checkout` from the commit rather than from memory.** Nineteen were caught.
  The two that were not are the handler's query -- the `.Where` deleted, and the
  order reversed -- and both are unreachable from a suite that may not open a
  database; both were checked by hand instead. Two rounds were needed and the
  first round is the part worth keeping.

  **The first sweep reported every mutation as caught, and every one of those
  verdicts was worthless.** The application was still running from the by-hand
  verification, holding `bin\Debug`, so `dotnet test` failed to *build* each
  time and the script read a non-zero exit as a dead mutation. #63 already
  recorded the fix for the underlying clash -- a separate output folder and a
  second port -- and the lesson this adds is about the harness rather than the
  application: **a mutation runner has to tell a failing test from a failing
  build**, or it reports a perfect score for a suite it never ran.

  The same harness has a second edge worth writing down, because it produced a
  red suite over code that was already correct: the sweep reverts the source and
  does **not** rebuild, so the next `dotnet test --no-build` runs the *last*
  mutation's binaries. Here that was `.AllowAnonymous()` on the export, which
  answers a signed-out request by reaching a database that is not there -- a 500
  where the test wanted a 401, on a working tree `git status` reported clean.

  **The second sweep found a test that looked like it asserted something.**
  Writing the date in the ambient culture survived a culture test that used
  `ro-RO` alone -- `-` is a literal in a custom format string rather than a
  separator placeholder, so Romanian renders `yyyy-MM-dd` exactly as the
  invariant culture does. `ar-SA` is what changes it: its default calendar is Umm
  al-Qura, so the same format string yields a **Hijri** year, which is #31
  arriving on the way out instead of the way in. Both cultures are now in the
  theory, for two different halves of the rule, and a second test asserts that
  `ar-SA` really does render a different year -- otherwise the day that stops
  being true, the date half quietly tests nothing.

  **Verified against the running compose stack**, which is where the half that
  matters is. Five transactions, one of which the rules categorised by itself;
  three corrected by hand, one of them corrected twice. The export is exactly
  those three, oldest first, with `X-Labelled-Rows: 3` -- the `rules` row absent,
  the twice-corrected row present once carrying its second label. `lidl, centru`
  came back quoted, a description stored with surrounding spaces came back
  quoted, and `78.5` came back as `78.50` from `numeric(18,2)`. Clearing a
  category dropped its row out of the export, which is #63's invariant doing the
  work. `python evals/score.py --set` read the export unedited; appended to a
  copy of `evals/transactions.csv` with `tail -n +2`, the scorer read 56 rows
  with no hand-editing. Anonymous is 401, a POST to the same path is 405.

  **And the #52 check, which is the one that has caught a real bug here before.**
  A second account exported one row and the first exported three, neither seeing
  the other's -- with no ownership condition anywhere in the query. That is the
  global query filter doing exactly the job that decided the endpoint over the
  `psql` script.

  **What is not automated, said plainly.** The whole client half: this project
  still has no test framework for React (#67 recorded the same for its own
  debounce), so the debounce-free button, the three states and the Blob download
  are checked by `tsc`, by `oxlint` and by reading. The download in particular is
  the piece most likely to be wrong and least likely to be caught -- an anchor
  that is clicked while detached is ignored by Firefox and works in Chrome, and
  an object URL revoked on the next line is a download that silently produces
  nothing in Safari; both are handled, and neither is exercised by anything that
  runs on its own. Signing in through a browser means typing a password, which is
  not something Claude does, so the screen was not opened.

  **Still deliberately open: the export does not deduplicate against
  `evals/transactions.csv`.** Exporting twice and appending twice puts every row
  in twice, and nothing reports it -- the scorer would simply weigh those rows
  double. What lost: sending the eval set up to be diffed against, which makes an
  export depend on a file in the repository and turns a read of one table into a
  merge; and a "since" parameter, which is state the application would have to
  keep about a chore. What is there instead is the date in the file name, which
  is a convention and not a control. It becomes worth fixing the first time
  somebody appends the same file twice.

- **Categorising happens after the save, driven by one nullable column and a
  five-second sweep -- decided 2026-09-02** (#92).
  `src/LandMoney.Web/Categorizing/CategorizerSweep.cs` is the worker,
  `PendingCategorization.cs` is the rule it selects on, and
  `transactions.categorization_attempts` is the marker. The client polls while
  anything on screen is still waiting. 36 new tests, and they still need no
  Postgres, no Docker and no network.

  **What it fixes is that the working case had become the slow one.** #39 put the
  categorizer call before `SaveChangesAsync` and #59 gave it a two-second connect
  budget, and both were right while the answer came from 109 substrings in 142 ms.
  With a model behind the port (#87) it is ~2.1 s of somebody's save, every time,
  and a timeout cannot bound a service that is working. So the row is written,
  answered for, and categorised afterwards.

  **A column and a sweep, not a queue in memory, and that was the decision the
  issue called its substance.** A `Channel<Guid>` read by a hosted service is less
  code, needs no migration and answers in milliseconds. It loses on where this
  runs: `--min-replicas 0` kills the process after about fourteen idle minutes
  (#35) and again at every revision, so anything queued and undone goes with it,
  silently. That is the fifth time this project has been offered a fallback whose
  absence nothing reports -- #39, #61, #62, #64 -- and the whole point of a column
  is that the owing outlives the process. An external queue lost to the arithmetic
  that killed the Redis cache in #87: a monthly charge against an application one
  person uses weekly.

  **`categorization_attempts` is one nullable column doing two jobs, and the null
  is the interesting half.** Null means nothing is owed; a number means something
  is, and how many attempts it has cost. A `pending` flag beside a counter was the
  obvious shape and loses because two columns can disagree -- `pending = false`
  with three attempts recorded is a state nothing should be able to produce, and
  the way to make that impossible is to have nowhere to write it.

  **What it must not be is `category IS NULL`, and that is #63's deferred decision
  coming due exactly where #63 said it would.** Clearing a category in the
  interface writes null to both category columns, so a row somebody deliberately
  cleared is indistinguishable from one nothing has touched -- and #63's own text
  says to reopen it "the day something re-categorises existing rows". This is that
  day. An explicit marker answers it without changing what clearing means: a
  cleared row was never marked as owing anything, so the sweep cannot see it. Rows
  predating the column are null for the same reason and are equally out of reach,
  which is correct rather than a gap -- nothing asked for them.

  **The retry ceiling counts attempts that could have been billed, not attempts
  that were made**, which is the whole of #92's fourth trap.
  `CategorizerOutcome.CountsAgainstTheCap` is the one place that rule lives:
  `not-configured` never opened a socket and `unreachable` is a refusal or a DNS
  failure, so neither reached a model and neither is charged; everything else is.
  Charging an outage would abandon rows for a failure that cost nothing.

  **`timeout` is charged and it is the uncomfortable one.** #64 records that a call
  the model answers at seven seconds is billed and still reads as a timeout here,
  and #39 measured that a *stopped container* also arrives as a timeout rather than
  as unreachable, because the SYN goes unanswered instead of being refused. So the
  branch genuinely cannot tell a free failure from a paid one. It is charged,
  because between "an outage abandons some rows, visibly and recoverably" and "a
  slow model bills for ever, silently", the first is the failure to choose. What
  makes that affordable is the other half: **a tick stops at the first answer it
  cannot use**, so an outage costs one attempt on the oldest row per five seconds
  rather than one on every owed row. Measured -- eight ticks against a stopped
  categorizer produced eight calls, not eight times twenty.

  **The guard is repeated in the UPDATE's own WHERE clause, and that is the one
  decision here about correctness rather than shape.** Rows are read at the top of
  a tick and each call takes about two seconds, so a batch is the better part of a
  minute during which somebody may correct a category on the screen. The entity in
  memory is a photograph taken before that happened, and `SaveChanges` would write
  the prediction over the correction -- #92's second trap arriving through
  staleness rather than through a missing check, and an in-memory `MayOverwrite`
  would not catch it either. `ExecuteUpdate` with the predicate repeated means
  Postgres evaluates the guard at the moment of the write. Measured, and the
  statement is worth keeping: `WHERE t.id = @id AND t.categorization_attempts IS
  NOT NULL AND t.categorization_attempts < @maxAttempts AND (t.category_source <>
  'human' OR t.category_source IS NULL)`. This is the caller
  `CategorySources.MayOverwrite` was written for in #63 and the first where it is
  not trivially true.

  **`IgnoreQueryFilters` is called for the first time in this repository**, and
  `AppDbContext`'s comment predicted the shape of the day it would be. The sweep
  has no `HttpContext`, so `CurrentUser` answers null, and `owner_id = NULL` is
  never true in SQL -- without the call the sweep would select nothing, for ever,
  looking exactly like a categorizer that was never reached. It is the mirror of
  #52's bug: there a null owner made one person's rows visible to everyone, here it
  would make everyone's rows visible to nobody. What makes ignoring the filter safe
  rather than merely necessary is that the operation has no owner: it reads a row,
  sends three fields to a service with no concept of accounts, and writes the
  answer back to the same row. **The day that stops being true is the day retrieval
  sends the owner's own history as examples** (#66), and that is the change which
  has to revisit it. What lost: a mutable `ICurrentUser` registered in the
  background scope, which reads as safer and makes a fake signed-in user a
  supported concept.

  **A third `CategorizerKind`, `sweep`, rather than reusing `save`.** Until now
  every `save` call happened inside the request that wrote the row; from here none
  do, so keeping the word would leave #64's summary reporting the same number for a
  different event -- the one failure a closed vocabulary exists to prevent -- and
  would throw away the signal that the change took. **`save=0` is now the correct
  reading and `save>0` means something categorises inline again.** Measured:
  `2 recorded (save=0, preview=0, sweep=2)`.

  **`CategorizerClient` now returns the outcome beside the answer**, for the sweep
  only. `CategorizerAnswer.Nothing` deliberately collapses "there is no
  categorizer", "it did not answer in time" and "it answered something unusable",
  which is exactly right for the two callers that only decide whether to store a
  category, and useless to a retry -- only one of those three could have been paid
  for. The two older signatures are unchanged, which is what the projection helper
  is for.

  **`categoryPending` on the wire, and it is not derivable from a null category.**
  The categorizer abstains on roughly a third of the labelled set, and an
  abstention is a final answer that leaves the category null for ever, so a client
  polling on `category === null` would poll for ever on exactly those rows. The
  flag is also false for a row the sweep has given up on. The client polls every
  two seconds while anything visible is pending, gives up after thirty fruitless
  polls, and **refills that budget whenever the number of pending rows goes down**
  -- so one save and a three-hundred-row import need no separate handling, and only
  a minute of no progress at all stops it.

  **What was measured, against the compose stack, and it is the half that matters.**
  Five rows seeded across two owners in the five states the predicate has to
  separate: a fresh row was categorised (`eating-out`/`rules`), a row belonging to
  a *different* owner was categorised too -- which is `IgnoreQueryFilters` working
  and nothing crossing between owners -- and three were untouched exactly as
  designed: one labelled `human`, one with a null marker (the #63 hole, shut), and
  one at the cap. The SELECT is
  `WHERE categorization_attempts IS NOT NULL AND categorization_attempts <
  @maxAttempts AND (category_source <> 'human' OR category_source IS NULL)`, with
  no `owner_id` in it -- so **EF Core's null-semantics rewrite is confirmed rather
  than assumed**, which was the one claim in this design that C# could not check.
  Then the categorizer was stopped: eight ticks, eight `timeout` outcomes at ~2.0 s
  each, attempts charged one per tick; restarted, and both rows resolved, so a
  short outage abandons nothing.

  **What is not automated, said plainly.** The sweep's own loop needs a database
  and a categorizer, which is the wall `AuthorizationTests` and #62 both document,
  so `SweepOnceAsync` is covered by the by-hand run above and not by the suite. The
  three acceptance tests that need a signed-in session -- a 201 that is as fast
  with the categorizer down as up, the category appearing without a reload, and the
  process killed between the two -- were **not** run: signing in means typing a
  password, which is not something Claude does. The client half has no test
  framework at all (#67 recorded the same for its own debounce), so the polling,
  the budget and the "Categorizing..." indicator are checked by `tsc`, by `oxlint`
  and by reading.

  **Checked by breaking it, per #21: twelve mutations, one at a time, reverted with
  `git checkout` from the commit rather than from memory.** Eleven were caught. The
  harness itself carries the two traps this repository has already paid for: it
  runs `dotnet build` before `dotnet test` and reports a build failure as invalid
  rather than as a kill -- #89's sweep scored a suite it never ran -- and it
  refuses a substitution matching zero or two places, which is #21's own lost
  mutation, and that refusal fired once here on the three identical `unusable`
  exits.

  Two are worth keeping. `Owing = 1` survived the first sweep: a row entering with
  one attempt already spent is still owed a category and still stops at the cap, so
  every existing test passed while every row's budget had silently been shortened
  by one. It needed a test naming what the count *means* rather than what it
  permits. And the null check in `ToResponse` is an **equivalent mutant** -- `null
  < 30` is false in C# either way, so no test can kill its removal; it is kept for
  symmetry with the SQL projection, which is now written beside it rather than left
  to be rediscovered by the next sweep.

  **Deliberately not done: the import.** #62 stores every row with a null category
  and says in as many words that "the backfill is its own issue" -- and its stated
  reason, that a 300-row file would be a request running for minutes, is exactly
  what this change removes. Marking imported rows as owing a category is one
  property assignment away and is not in this pass.

## How work flows

Agreed 2026-08-05. This replaces committing straight to `main`, for Claude as
much as for the owner.

1. **Claude opens an issue** per task: what to do, how it is verified, and the
   traps worth knowing in advance. Issues are the queue; `docs/roadmap.md`
   stays the map.
2. **The owner writes the code** on a branch off `main`
   (`feature/transaction-model`), and opens a pull request saying `Closes #N`.
3. **Claude reviews the diff.** The rules above still hold: name the
   alternative that lost, show the real error output rather than paraphrasing.
   Where there is something to run, run it: the review of #19 read correct and
   the endpoint still returned an amount the database had rounded away, which
   no amount of reading the diff would have shown. A review of an endpoint
   sends requests to it; a review of a schema reads the running database.
4. Merge closes the issue.

Nobody commits to `main` directly. CI runs on the pull request, which is the
entire point of having it.

**What Claude does not do here:** authenticate. `gh auth login`, tokens and
passwords belong to the owner, are never typed by Claude, and never appear in
an example command. Creating the Projects board is the owner's job too -- it
needs a token scope a normal login does not grant.

## Where the project lives

`D:\Work Home\LandMoney`, moved there from the desktop on 2026-08-05, with
netshift beside it in `D:\Work Home\AI`.

Worth knowing for next time: a Python virtual environment does not survive a
move. Absolute paths to the interpreter are baked into `.venv`, so it breaks
quietly rather than loudly, and has to be recreated with `uv sync`. Unlike
`bin/` and `obj/` in .NET, which simply rebuild.

## Repository and machine setup

Things that live outside the code and are invisible in a diff, so they are
easy to lose. All true as of 2026-08-11.

- **Commit identity must be `landcovschi@gmail.com`.** GitHub links a commit to
  an account by the email address in it, so a commit authored under another
  address is not attributed and does not appear in the contribution graph. The
  machine's global config was `landcovschi@yandex.ru` and one commit went out
  under it before this was noticed; the global config now says gmail. Claude
  uses whatever git is configured with and does not substitute an address.
- **`delete_branch_on_merge` is on.** Merging a pull request removes its branch
  on the server. Locally, `git fetch --prune` clears the stale references and
  `git branch -d` (lower case, refuses unmerged work) removes the branch
  itself.
- **There is a ruleset on `main` as of 2026-08-24**, named `main`, active. It
  replaces the note that used to say there deliberately was not one, on the
  grounds that required status checks had nothing to require; #22 landed the
  workflow and it was seen both green and red on a pull request first.

  What it says, and the parts of it that are not obvious from the form that
  created it:

  - **The required check is `build`** -- the job, not `CI`, the workflow. It is
    also bound to `integration_id` 15368, which is the `github-actions` app, so
    the name is not satisfiable by some other app reporting a check that happens
    to be called `build`.
  - **The bypass list is empty, so the rule applies to the owner too.** Worth
    saying out loud because it is the opposite of the old Branch protection
    rules, where an administrator bypassed everything until "Do not allow
    bypassing" was ticked. Rulesets start closed.
  - **Targets `~DEFAULT_BRANCH` rather than the literal string `main`**, so it
    follows a rename instead of quietly protecting nothing.
  - `strict_required_status_checks_policy` is off -- branches are not forced to
    be up to date before merging, which on a one-author repository would only
    mean re-running the check on every open pull request each time `main` moves.
  - Restrict deletions and Block force pushes came ticked by default in the new
    ruleset form and were kept. They line up with the permissions file, which
    leaves force-push and history rewriting out on purpose.

  **Not enabled: "Require a pull request before merging."** So "nobody commits
  to `main` directly" is still a written rule rather than an enforced one. That
  was a deliberate choice on the grounds that #22 did not ask for it.

  **The trap to avoid for as long as this rule is on:** never add `paths:`
  filters to the triggers in `ci.yml`. A pull request touching nothing in the
  filter never starts the workflow, `build` therefore never reports, and a
  required check that never reports blocks the merge for ever at "Expected --
  Waiting for status to be reported". The same failure follows from a typo in
  the check name. Both are fixable only by editing the ruleset.

  **A job skipped by `if:` is not the same failure, and the difference was
  measured on #42 rather than reasoned about.** #24 gave `ci.yml` a second job,
  `publish`, gated on `if: github.event_name == 'push'`. The guess was that it
  would report nothing on a pull request and deadlock the merge the way a
  `paths:` filter does. It does not:

      $ gh api repos/landcovschi/LandMoney/commits/<sha>/check-runs
      {"conclusion":"skipped","name":"publish","status":"completed"}
      {"conclusion":"success","name":"build","status":"completed"}

  The workflow *did* start, so the job reports -- `completed` with conclusion
  `skipped`, which GitHub counts as satisfying a required check. The deadlock
  above needs the workflow never to start at all: a `paths:` filter, a
  conflicting pull request, or a name that matches no job.

  So **adding `publish` to the required checks would not block anything -- it
  would be worse, a required check that passes on every pull request without
  ever running.** `build` stays the only required check, for that reason rather
  than the one first written here.

  **A third way into that same symptom, met in #23 and not caused by the
  ruleset at all: a conflicting pull request gets no run either.** The
  `pull_request` event builds `refs/pull/<n>/merge`, and a branch that conflicts
  with `main` has no such ref to build, so the workflow never starts and
  `gh pr checks` answers `no checks reported on the '<branch>' branch`. That is
  the same sentence a `paths:` filter produces and it means something entirely
  different -- here the fix is to merge `main` into the branch, not to touch the
  workflow or the ruleset. The tell is `gh pr view --json mergeable`, which says
  `CONFLICTING / DIRTY`; the checks list alone cannot distinguish the two.
- **`dotnet` and `node` versions are pinned in files, not in the workflow.**
  `global.json` says 10.0.400 with `rollForward: latestFeature`;
  `src/landmoney.client/.nvmrc` says `24`. Both are read by the CI workflow
  rather than restated in it, which is the point: a version typed into
  `ci.yml` drifts from the one installed here and nothing reports it. The
  looseness is deliberate and matched -- `.nvmrc` names a major, `global.json`
  names a feature band and accepts anything above it inside .NET 10. What lost:
  `latestPatch`, the default when the key is absent, refuses 10.0.5xx and turns
  a routine Visual Studio update into a build error that reads like a broken
  repository; `disable` makes every SDK update a file edit.

  Measured in the first green run rather than assumed: **`setup-dotnet` installs
  the exact version named in `global.json` and does not apply `rollForward`.**
  The runner is therefore pinned to 10.0.400 outright, and `rollForward` is a
  rule for this machine and for the Dockerfile in #23.
- **`dotnet-ef` is pinned in `.config/dotnet-tools.json`.** A fresh clone needs
  `dotnet tool restore` before any `dotnet ef` command.
- **Postgres is published on 5433**, not the default. Inspect the schema
  without installing anything:
  `docker compose exec postgres psql -U landmoney -d landmoney -c '\d transactions'`.
  No quotes needed since the schema went `snake_case` on 2026-08-18 -- before
  that the table was `Transactions` and the unquoted form answered `Did not
  find any relation named "transactions"`, which reads like the migration never
  ran. `README.md` has the same connection details for a desktop client.
  Docker Desktop has to be running first; it usually is not.
- **Node is the LTS line, installed with `winget install OpenJS.NodeJS.LTS`**
  (24.19.0 as of 2026-08-23), not `OpenJS.NodeJS`, which is the current major
  and supported for six months against the LTS thirty. The same reasoning that
  picked .NET 10 LTS. Two things learned installing it: the winget id has to be
  the full one -- a short `publisher.name` guess matches `winget search` and
  fails `winget install` -- and a tool installed while a process is already
  running is invisible to that process, because PATH is read at start. Claude's
  shells therefore cannot see a program the owner just installed until the
  session is restarted.
- **The app listens on 5150 (http) and 7063 (https)**, and the Vite dev proxy
  targets `http://localhost:5150`. Both launch profiles publish 5150, so either
  one works, from a terminal or from Visual Studio with the debugger attached.

  **`UseHttpsRedirection` is gated to non-Development, decided 2026-08-23.**
  This replaces the answer #4 settled on, which was to keep the redirect and
  always pass `--launch-profile http` -- a profile with no https port, so the
  redirect found none and degraded to a no-op. That worked and it made the
  requirement invisible: Visual Studio's run dropdown prefers `https` for a
  project that has one and cannot be told otherwise, so F5 broke the client and
  the symptom was a CORS error naming neither the profile nor the redirect. A
  rule that only holds when someone remembers a flag is not a rule.

  The line now sits beside `UseHsts`, which was already gated for the same
  reason: both exist to keep real traffic out of the clear, and in development
  there is none -- two loopback ports and a certificate this machine issued to
  itself. Having one gated and not the other was the actual inconsistency.

  What lost, again: the proxy pointing at `https://localhost:7063` with
  `secure: false`. It works, and it moves knowledge of the development
  certificate into the client's config while teaching a browser client to accept
  a certificate it did not verify -- a setting to keep out of a file that ships.
  The price of the route taken is that development no longer exercises the
  redirect, so a mistake in it would first appear in production; small, because
  behind Container Apps the ingress terminates TLS and http never reaches the
  process.

  **A port other than 5150 means the profile was not applied.** Starting
  `bin\Debug\net10.0\LandMoney.Web.exe` directly gets 5000: `launchSettings.json`
  is a tooling file that `dotnet run` and the IDE read and pass on through
  environment variables, and the app has never heard of it. Visual Studio does
  launch that same exe, which is why a VS session shows a bare exe in its command
  line and still listens on 5150. The API is up and answering the whole time,
  just not where the proxy looks, so the screen reports it unreachable and is
  right. Read the port off `Now listening on:` rather than trusting that it
  started.
- **`dotnet test LandMoney.slnx` runs everything.** `dotnet test` reads an
  `.slnx` directly, so there is one command and no project path to remember.
  It needs nothing running: the suite is unit tests over the validation rules,
  and the database is never opened.

- **The connection string lives in user-secrets**, never in a committed file,
  and carries `Timeout` and `Command Timeout`. A network client without a
  timeout turns an outage into a hang.
- **Permissions are prefix rules in `.claude/settings.json`**, committed, so
  they work on any machine. They cover the ordinary loop: git inspection and
  the branch-commit cycle, `gh` for issues and pull requests, `dotnet`
  build/test/restore and the EF Core migration commands, `docker compose up`
  and its read-only subcommands. Force-push, history rewriting, hard resets,
  `docker compose down -v`, `dotnet ef database drop` and `docker compose exec`
  are left out deliberately -- a confirmation is worth having there, and the
  last one runs anything at all inside the container.

  Two things about how this works, both learned the hard way. Settings are read
  when a session starts, so a rule added mid-session does not take effect until
  the next one. And pressing "always" on a prompt records the **entire command
  string**: do that to a compound one-liner and the rule matches that exact
  invocation and nothing else. Thirteen such dead rules accumulated in
  `settings.local.json` before anyone noticed.

  Turning the confirmation off altogether is a session permission mode, not a
  settings file, and it belongs to the owner. Claude cannot and should not
  grant itself permissions.

## How Claude should issue shell commands

One command per call. Not `cd "D:\Work Home\LandMoney"; echo "=== x ==="; git
status; git log -1 | Select-Object -First 3`.

The reason is mechanical rather than aesthetic. Claude Code recognises
read-only commands such as `git status` and allows them without asking, but a
compound line beginning with `cd` is not recognisable as any of them, so it
prompts -- and the "always" the owner then presses saves the whole string,
which never recurs. Out of 454 shell calls in the first two weeks, 128 began
with `cd`, and roughly 109 were read-only git and `gh` commands that should
never have prompted at all.

Use `git -C <path>`, `--project`, `-f` and the equivalents instead of changing
directory. Skip the `echo` banners. Accept more tool calls in exchange for
each one being recognisable.

**An argument that looks like a Unix path goes through PowerShell, not Git
Bash.** Bash rewrites it into a Windows path before the program sees it, and
every instance so far failed as an error about something else entirely:
`az role assignment create --scope "/subscriptions/..."` answered
`MissingSubscription` (#38), `docker run -w /w` answered
`the working directory 'W:/' is invalid` (#37). `MSYS_NO_PATHCONV=1` in front
fixes it and is the way to confirm the diagnosis in one command; the PowerShell
tool never had the problem. Twice is a coincidence, three times with #53's
`curl.exe` is a rule.

## Open decisions with a deadline

Recorded here because a comment on a merged pull request is not somewhere
anyone will look again.

**There are none open today.** The one that was here was closed on 2026-08-28 in
#59, on time -- the account below is kept because the *shape* of the argument is
the reusable part, and because a section that has only ever held one entry reads
like a section nobody uses.

**`transactions.category_source` -- opened 2026-08-26 (#39), closed 2026-08-28
(#59), in the change that put a model behind the port and before it was switched
on.**

The column, the migration and the write path all landed in #59, ahead of the
adapter in the same branch, which is what the deadline asked for in as many
words. The rows that already existed were **backfilled to `rules`**, and that was
argued rather than done quietly: it is provably true, not merely defensible --
`CreateTransactionRequest` has never carried a category field, `CategorizerClient`
is the only writer, and it had only ever spoken to a service whose one predictor
was `RulesPredictor`. Leaving them null was the alternative and it lost on saying
less than the evidence supports, and on making `category_source IS NULL` mean two
things at once. The `WHERE category IS NOT NULL` clause is what keeps the
remaining meaning clean: **a source exists exactly when a category does**, which
was checked against the running database rather than reasoned about -- 2 rows
backfilled of 21, 19 untouched, zero violations of that invariant.

What it does *not* record, and this is the honest limit of the whole exercise:
the 21 rows predating the column still say `rules` because they must have been,
not because anything observed it. The deadline was never about those rows. It was
about the rows that would have been written *after* a model started answering and
*before* anyone thought to add the column, and there are none, because the column
came first.

The original entry, kept for the argument rather than the status:

The categorizer answers `{category, source}` and `source` is `rules` today. The
.NET side reads it, logs it and **does not store it**. There is no column.

Why that is safe now: nothing else can produce a category. `CLAUDE.md` forbids a
model call until a baseline is scored, so "written before the model adapter
existed" is a complete and correct answer for every row in the table -- the
information is recoverable from the date. Adding a column for a value that has
one possible setting would be schema for its own sake, and #39's checklist did
not ask for one.

Why it has a deadline rather than being closed: the moment a second producer
exists, that reasoning stops holding **retroactively for the rows written in
between**. There is no query and no migration that can recover which of two
things wrote a category once both are running -- the fact was never recorded.
This is the same argument that put `source` in the HTTP response before there
was anything but rules to report, and it is the one part of #39 that cannot be
fixed afterwards.

So: **the column, the migration and the write must land in the same change as
the model adapter, and before it is switched on** -- not in the change after it,
and not "when we start comparing". The comparison is the thing that needs the
data, and by then it is too late to collect it.

*(Done, #59, 2026-08-28. See the note above this block.)*

The two that used to be here -- the schema naming and the day boundary -- were
settled on 2026-08-18 in #13 and #17, and both records live in "The stack, and
what was rejected" above.

## Keeping context between sessions

The owner asked for this explicitly, and it is the rule that made the
predecessor project survivable: **a chat thread is not memory.**

- Decisions with a non-obvious alternative go in this file or in
  `docs/roadmap.md`, together with what lost and why.
- Progress goes in the roadmap checkboxes, ticked in the same session the work
  happened.
- Anything surprising in the code gets a comment next to the surprising line.
- A fact that exists only in the conversation is already lost. Write it down
  before the session ends, not after.

## Session hygiene

The repository is the project's memory; a chat thread is not. Anything that
must outlive the session belongs in a file: a roadmap checkbox, an ADR, a
commit message, a comment next to the surprising line.

One session covers one coherent unit of work and ends at a commit. Before
that: run the build and the tests, tick what got done in `docs/roadmap.md`,
and write down any decision whose alternative was non-obvious -- including the
alternative and why it lost.

## Reporting

- A test failed: show the output, do not paraphrase it.
- Something is unfinished: say exactly what and why.
- Unsure: say so. A confidently wrong answer costs more than usual here,
  because the owner cannot yet check the Python and Azure halves.
