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
  No authentication, no roles, no admin panel, no reporting until the AI work
  is running. The whole point of the rewrite was to stop polishing the part
  that was already comfortable.
- Do exactly what was asked. Spotted an adjacent problem? Mention it, do not
  fix it in the same pass.
- A database gets added when it has a job, not to have seen it. Postgres now;
  Redis when there are model responses worth caching.
- No new dependency without discussing it.
- **No LLM call before evals exist.** Hand-labelled transactions and a
  rules-based baseline come first. Without them "it got better" is a feeling,
  not a fact. This is the single rule carried over from netshift unchanged.
- Never touch `.env`, never print its contents, never put a key or a
  connection string into an example command.

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
- **Image registry: `ghcr.io`.** Azure Container Registry does the same job
  and costs around 5 USD a month for Basic; GitHub's is free for public
  repositories.
- **Hosting: Azure Container Apps.** GitHub cannot host this -- Pages serves
  static files only, and this needs a live process plus a database. Container
  Apps was picked over App Service because a second container (the Python
  categorizer) is coming, and it scales to zero when idle.
- **Database: Postgres.** Azure Database for PostgreSQL is not free; while
  learning, Postgres runs as a container next to the app. That is not how
  production is run and the difference is to be understood, not glossed over.

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

## Open decisions with a deadline

Recorded here because a comment on a merged pull request is not somewhere
anyone will look again.

Nothing is open right now. The two that were here -- the schema naming and the
day boundary -- were settled on 2026-08-18 in #13 and #17, and both records live
in "The stack, and what was rejected" above. The heading stays so the next one
has somewhere to go.

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
