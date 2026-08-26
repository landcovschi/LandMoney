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

- **Authentication: OpenID Connect in the application, with a cookie -- decided
  2026-08-26** (#52). Provider-agnostic: `Authentication:Oidc:Authority`,
  `ClientId` and `ClientSecret` are configuration, and no file in `src/` names a
  provider. The wiring is `src/LandMoney.Web/Auth/AuthenticationSetup.cs`, the
  registration commands are step 15 of `docs/deploy-azure.md`, and the owner
  creates the registration -- Claude does not, for the same reason it does not
  run `gh auth login`.

  **What lost, and the first one lost for a reason this repository has already
  paid once.** *Container Apps built-in authentication (Easy Auth)* is the
  cheapest thing that closes the hole and needs no application change at all. It
  makes authentication a property of the host: under `docker compose`, behind the
  nginx container expected when the categorizer arrives, or anywhere that is not
  Container Apps, there would be no authentication and nothing reporting it.
  That is the identical argument #36 used to keep `UseHsts` and
  `UseHttpsRedirection` in the application rather than trusting the ingress, and
  the thing being lost silently here is a closed door rather than a header.
  *ASP.NET Core Identity* lost on surface: a user table, password hashing, reset
  flows and email delivery to arrange, for one user, in the slice that is meant
  to stay thin.

  **The cookie, not a token in the browser.** The client is served by this same
  application out of `wwwroot` (#20), so a cookie is same-origin: no CORS, no
  refresh logic in TypeScript, and no access token anywhere JavaScript can read.
  `SaveTokens = false` for the same reason -- nothing here calls anything on the
  user's behalf, and keeping the access token would put it in a cookie sent with
  every request.

  **The three branches, and the order they are tested in is the security
  property.** Configured -> OpenID Connect. Not configured and Development -> a
  local scheme that signs every request in as `local-development-user`. Not
  configured and anything else -> a scheme that authenticates nobody, so every
  protected request is 401.

  The configured case is tested **first**, and that is what keeps
  `ASPNETCORE_ENVIRONMENT` from being a door. #36 recorded that four middlewares
  hang off `!IsDevelopment()`, so a typo in that variable turns four things off
  at once and says nothing; authentication would have been a fifth and by far the
  worst. It is not, because the deployed app has an Authority and therefore takes
  the first branch whatever the environment claims to be. Reaching the
  development sign-in in Azure would mean deleting a secret, not mistyping a
  word.

  **There is no `?? throw` for a missing Authority, and that is #57's rule being
  applied rather than re-learned.** `efbundle` runs `Program.cs` from a directory
  holding nothing but itself, in Production, with no `appsettings.json` -- which
  is exactly how a required-configuration throw for `Categorizer:BaseUrl` killed
  a deployment at `Apply migrations`. A throw here would have been the same
  landmine in the same job. So the process starts either way and the fail-closed
  behaviour moved from startup to the request: it serves nothing, and it logs an
  error saying why. Verified by building a win-x64 bundle and running it from an
  empty directory -- the host builds, the failure is `No such host is known`, and
  `ci.yml`'s "The bundle must start without appsettings.json" step passes it.

  **The obvious cookie event is the wrong one, and it fails as a JSON parse
  error.** Every example of the API-versus-page split uses
  `CookieAuthenticationEvents.OnRedirectToLogin`. Here that is dead code: the
  challenge scheme is OpenID Connect, so an unauthenticated request is handled by
  that handler and the cookie handler is never consulted. The split has to be
  made in `OnRedirectToIdentityProvider`, with `HandleResponse()` to stop the
  handler writing its redirect over the 401 -- without that call the event
  appears to do nothing at all. Written the wrong way, `fetch` follows the 302 to
  the provider, is answered with a sign-in page, and the client reports a JSON
  parse error about a request that was refused for a reason it never saw.

  **Ownership is a global query filter, not a `.Where` per query.** #52's
  sharpest line is "every query gains a filter, and the one that forgets it is a
  data leak rather than a bug", and a filter written by hand is correct today,
  with one query, and one new endpoint away from being wrong for ever with
  nothing reporting it. `HasQueryFilter` makes the filter the default and makes
  the exception something that has to be asked for by name --
  `IgnoreQueryFilters`, a call that shows up in a diff and can be grepped for.
  Nothing in `src/` calls it. `SaveChanges` is overridden to stamp the owner on
  added rows for the mirror-image reason: a filter protects reads and does
  nothing about writes, and `TransactionEndpoints.CreateAsync` deliberately never
  mentions `OwnerId`.

  **The filter compares to NULL when nobody is signed in, and that is the
  fail-closed half.** `owner_id = NULL` is never true in SQL, not even for a row
  whose `owner_id` is also NULL, so an unauthenticated context reads nothing
  rather than everything. It is also the single easiest thing here to break with
  a well-meant "skip the filter when there is no user", which is why a test
  asserts the filter is still emitted for a null owner.

  **`OwnerId` is nullable and nothing is backfilled.** The database does not know
  who entered the rows written before #52 -- the fact was never recorded -- and a
  non-nullable column would need a value invented at migration time, which is a
  claim about ownership that is not true. The consequence is concrete and reads
  as data loss if it is not expected: after the migration and before the claiming
  `UPDATE` in step 15 of the runbook, **the site is empty**. The rows are all
  still there.

  It is also not a foreign key and there is no users table. A row needs to know
  which subject owns it; nothing here lists users or relates anything else to
  them. The price is that the owner is the provider's `sub`, so **changing
  provider orphans every row** -- recoverable with the same one-line `UPDATE`,
  and written down rather than designed around.

  **The index was replaced, not extended, and this is #37's own argument applied
  to a changed query.** The list is now
  `WHERE owner_id = @p ORDER BY occurred_at DESC, created_at DESC`, and an index
  that does not start with the equality column cannot serve the filter -- so
  `ix_transactions_occurred_at_created_at`, correct for #37's query, answers
  nothing about this one. Measured the way #37 measured it, with
  `SET enable_seqscan = off`: `Index Scan Backward using
  ix_transactions_owner_id_occurred_at_created_at`, `Index Cond` on `owner_id`,
  and no sort step. The old index is dropped rather than kept beside it, because
  the query filter guarantees every query is now filtered by owner and a second
  index is paid for on every write.

  **That migration drops an index, which the migrate-first rule did not cover.**
  #37 records "migrate first, then deploy the revision... safe while every
  migration only adds". This one does not only add, and it is still safe, for a
  reason worth separating out: dropping an *index* can only change a plan, never
  an answer, so the old revision briefly running against the new schema is
  slower at worst. Dropping a *column* is the case that rule is really about, and
  there is still none.

  **`Microsoft.AspNetCore.Mvc.Testing` is in, and #21 named this as the day.**
  #21 refused it because an `IEndpointFilter` is an object with one method and
  `EndpointFilterInvocationContext.Create` builds its argument. Authorization is
  the opposite: whether an anonymous request is refused depends on the order of
  two middlewares, on metadata hung on an endpoint, and on which of the three
  branches above was taken -- none of it reachable from anything smaller than the
  assembled application. 33 new tests, 103 in total, and **they still need no
  Postgres, no Docker and no network**: every request they make is refused before
  a handler runs, or is answered by `/api/me`, which reads the principal and
  nothing else. The ownership filter is asserted through `ToQueryString()`, which
  translates the expression without opening a connection.

  **Two traps inside the test factory, both of which read as something else.**
  `ConfigureAppConfiguration` is applied *after* `Program.cs` has already read
  its configuration, so settings arrive correctly and too late, and every test
  fails with `ConnectionStrings:Default is not set` -- which reads as a test that
  forgot to set it. `UseSetting` is early enough. And setting
  `OpenIdConnectOptions.Configuration` to avoid discovery does nothing: the
  framework's own post-configure has already built a `ConfigurationManager` from
  `Authority`, and the handler prefers the manager whenever it has one. The type
  that means "here is the answer, never go and ask" is
  `StaticConfigurationManager<T>`, and without it the tests reach the internet and
  fail with `IDX20803: Unable to obtain configuration from`.

  **What is deliberately still open: Data Protection keys are not persisted.**
  The authentication cookie is encrypted with keys generated in memory, so they
  die with the process -- and with `--min-replicas 0` the process dies after
  about fourteen idle minutes (#35). The practical rule is that coming back to
  the site after a pause means signing in again, which with a live provider
  session is a redirect and no typing. Two sharper edges of the same cause: a
  revision replaced mid-sign-in fails the callback with `Correlation failed`, and
  the day `--min-replicas` goes above 1 the symptom becomes "signed out at
  random" because two replicas cannot read each other's cookies. The fix is
  `PersistKeysToAzureBlobStorage` plus `ProtectKeysWithAzureKeyVault` -- two
  packages and another Azure resource, which is a deployment decision with a bill
  attached rather than part of closing the door.

  **The categorizer stays user-unaware.** #52 said its boundary "changes shape:
  it is either called by the API on behalf of a user, or it needs to know about
  users itself". It is the first, and no identity crosses the wire: the service
  is sent a description, an amount and a currency, exactly as in #39, because it
  has no use for who is asking and sending it would put user identity into a
  second service's logs for nothing. That changes at #66, where the user's own
  history becomes few-shot examples -- and that is the issue where it has to be
  reopened, not this one.

  **`/index.html` is public and `/` is not.** `UseStaticFiles` is not an endpoint
  and consults no authorization metadata, so `wwwroot` is served to anyone at any
  position in the pipeline; `/` is protected because it is matched by
  `MapFallbackToFile`, which is an endpoint. Deliberate: the shell is the same
  bytes for every visitor and holds no data, exactly as public as the JavaScript
  bundle it loads, and the only thing behind it refuses an anonymous request.

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

**`transactions.category_source` -- opened 2026-08-26 (#39), due before the
model adapter (#39's step 4) writes its first row.**

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
