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
- [x] Image pushed to `ghcr.io` -- #24, 2026-08-24. A second job in
      `ci.yml`, `needs: build` and `if: github.event_name == 'push'` -- which
      reads as "pushes to main" because the trigger is already narrowed there,
      and keeps reading that way if the branch is renamed. `setup-buildx` ->
      `login` -> `metadata` -> `build-push`, tagged `sha-<40>` always and
      `latest` on the default branch

      **`permissions` is on the job, not the workflow**, which is the trap #24
      names and the reason the workflow-level block stayed `contents: read`. A
      job-level block *replaces* the workflow-level one rather than adding to
      it, so `contents: read` is restated beside `packages: write` or the
      checkout loses its token -- and that same replacing is what keeps a token
      that can push images away from the job that runs `npm ci`

      **The action majors were checked and not one of them was what memory
      said.** `login-action@v4`, `metadata-action@v6`, `build-push-action@v7`,
      `setup-buildx-action@v4`; from memory they would have been v3, v5, v6 and
      v3. The same lesson #22 already recorded for `checkout`/`setup-node`/
      `setup-dotnet`, so it is now twice

      **`setup-buildx-action` is required rather than decorative**, and its
      absence is silent: `cache-to: type=gha` is a buildx cache exporter and the
      default `docker` driver has no exporters, so the build and the push both
      succeed and the cache is simply never written. The only symptom is that
      the next run is not faster. `mode=max` for the same reason in the other
      direction -- the default `min` exports only the final stage's layers, and
      this Dockerfile's expensive layers (`npm ci`, `dotnet restore`) are in the
      two stages that get discarded

      **The lower-case trap, measured:**
      `docker build -t ghcr.io/landcovschi/LandMoney:local-24 .` answers
      `ERROR: failed to build: invalid tag "...": repository name must be
      lowercase`. `${{ github.repository }}` survives as the `images:` input
      only because `metadata-action` sanitizes it -- its README promises to
      lowercase the image name -- so nothing else in the job may name the image
      except through the `meta` outputs

      **Two things this cannot verify until it has run on `main`,** and they are
      the whole of #24's "verified by": the package is **private by default even
      though the repository is public** and has to be made public once, by hand,
      in the package settings -- otherwise slice 3 needs a pull secret it should
      not need; and `docker pull ghcr.io/landcovschi/landmoney@sha256:...` from a
      machine that has never logged in. The digest is written to the run summary
      so that check is a copy rather than a hunt through the log

      What is verified now: the workflow parses, `docker build` from the
      repository root still succeeds against the unchanged `Dockerfile` of #23,
      and the lower-case failure above was reproduced rather than recalled

      **One prediction was wrong and the pull request said so.** A job skipped
      by `if:` was expected to report nothing and deadlock a required check the
      way a `paths:` filter does. It reports `completed` / conclusion `skipped`,
      which GitHub counts as satisfying the requirement -- the deadlock needs
      the workflow never to *start*. Corrected in `CLAUDE.md` with the
      `check-runs` output beside it

Azure Container Registry was the first plan and lost to `ghcr.io`: same job,
but ACR Basic costs around 5 USD a month while GitHub's registry is free for
public repositories. One fewer paid service, one fewer set of credentials.

**Done when:** a fresh clone builds and tests on a machine that is not this one.

## Slice 3 -- deploy

**Skill:** the gap netshift never closed. This is the "CD" the owner asked for.

- [x] **Where Postgres lives once this is deployed** -- #34, 2026-08-25.
      A decision, no code: **Azure Database for PostgreSQL Flexible Server**,
      Burstable `Standard_B1ms`, 32 GB storage, no high availability,
      PostgreSQL 17 to match the local `pgvector/pgvector:pg17`. Free for twelve
      months on the free account (750 h/month of B1ms plus 32 GB storage and
      32 GB backup -- more hours than a month has, so it runs continuously),
      then roughly 15-20 USD a month. The subscription does not exist yet, so
      **the twelve months start in #35**, not now.

      **The deployed arrangement, which is the thing to hold in mind from
      here on:** one Container App pulling from `ghcr.io`, scaling to zero,
      talking over TLS to a managed Postgres that does *not* scale to zero.
      `docker-compose.yml` stays exactly as it is and is now the *development*
      database only -- "it works locally" and "it works deployed" have stopped
      being the same sentence, and everything from #35 onward is about the
      second one

      The alternatives and their real costs are written into `CLAUDE.md` the
      way #13 and #17 are. In short: **Postgres as a second container app** lost
      hardest -- the Container Apps dev-service add-on was retired 2025-09-30,
      an Azure Files volume is SMB and Postgres's `fsync`/locking assumptions
      are not what SMB offers, and no volume at all makes slice 1's "a
      transaction survives a restart" *true locally and false deployed*.
      **Neon's free plan** is the one to reopen when the free year ends, and it
      lost on purpose rather than on merit: it is free forever and inside every
      limit for weekly single-user use, and moving the database out of Azure
      removes the half of this slice worth learning. **Supabase** pauses a free
      project after a week with no requests, which for weekly use fails as "the
      site is down"
- [x] **The first deployment, by hand** -- #35, 2026-08-25. It is live:
      `https://landmoney.redstone-8c11320c.polandcentral.azurecontainerapps.io`
      serves the client at `/`, JSON at `/api/transactions`, and a transaction
      entered through the form survives `az containerapp revision restart`. One
      Container App on the SHA-tagged `ghcr.io` image with `--min-replicas 0`,
      one Flexible Server B1ms, one resource group. **Every command is in
      `docs/deploy-azure.md`**, which is the deliverable -- #38 transcribes it

      **`ghcr.io` is anonymously pullable**, which #24 could not verify until it
      had run on `main`: an unauthenticated registry token lists the tags, so the
      container app needs no pull secret and #24's last open item is closed

      **The region is `polandcentral`, and that was forced.** A new subscription
      is `OfferRestricted` for Postgres in West Europe *and* Germany West Central
      -- `list-skus` reports it, `create` finds out four minutes in. West Europe
      was chosen on breadth of service availability and lost to being
      unavailable, which is not an argument that can be had

      **Four `az` flags have moved from what every tutorial still shows**, each
      failing as an argument error that reads like a broken command:
      `--high-availability` is now `--zonal-resiliency`, `--database-name` is
      elastic-clusters-only, `az provider register` takes **one** `--namespace`
      and silently drops the rest, and `az login` itself crashes on a fresh
      account inside its interactive picker (`core.login_experience_v2=off`)

      **`--public-access None` disables public networking**, rather than meaning
      "public, with no rules yet". The failure surfaces two commands later as
      `Firewall rule operations are not supported for a server without public
      access enabled` -- a message about firewalls, for a cause elsewhere.
      Recoverable with `update --public-access Enabled`; no server recreated

      **Both of #23's predictions were confirmed on the first start**, word for
      word: `Failed to determine the https port for redirect` and `Cannot load
      library libgssapi_krb5.so.2`. Recognised rather than investigated, which
      is what writing them down was for. `Hosting environment: Production` with
      nothing set, also as measured

      **Cold start from zero: 23.3 s, against 0.23 s warm** -- a factor of a
      hundred, and the honest price of `--min-replicas 0`. Scale-in itself took
      about **fourteen** minutes, not the `cooldownPeriod: 300` the scale block
      declares, so the cooldown is a floor rather than a schedule

      **That number collides with the client.** `transactions.ts` sets
      `REQUEST_TIMEOUT_MS = 10_000`, with a comment calling ten seconds
      "generous for a Postgres on the same machine" -- true when written, and
      not true here. Opening the URL cold is fine, because the *document*
      request absorbs the 23 s and a browser has no timeout of its own on a page
      load; by the time the client's first `fetch` runs, the container is warm.
      What breaks is a **tab left open**: after ~14 idle minutes the next
      `fetch` from an already-loaded page meets a cold container, gives up at
      10 s and shows the timeout message, and a retry succeeds because the first
      attempt started the container. Not fixed in #35 -- it is adjacent, and the
      obvious fix (raise the constant) makes a real hang take longer to report,
      which is what the timeout is for. It lands on this slice's own bar,
      **"the URL works from a phone"**, where the first interaction after a
      pause is exactly this case

      **Taken early from #36 on purpose:** the connection string went in as a
      Container Apps secret referenced by `secretref:`, not as a plain
      environment variable. Doing it the other way round would leave the password
      readable in `az containerapp show` until #36 got to it. The rest of #36 is
      untouched -- `ASPNETCORE_ENVIRONMENT`, `ForwardedHeaders`, the README note
- [x] Azure Container Apps, deployed from GitHub Actions, pulling from
      `ghcr.io` -- by hand first in #35, automated in #38, 2026-08-26. The order is
      deliberate: a deployment written straight into a workflow fails inside a
      runner where nothing can be inspected. Two things #34 leaves on the mat
      for #35: a Container App on the Consumption workload profile has **no
      stable outbound IP**, so a Flexible Server firewall rule pinned to an
      address will not hold -- the choice is a 0.0.0.0 "any Azure service" rule,
      which admits every Azure tenant and not just this subscription, or VNet
      integration with a delegated subnet; and `pgvector` is allowlisted in the
      `azure.extensions` parameter under the name **`vector`**, worth doing when
      the server is created rather than as a restart in slice 5

      **Done: revision `landmoney--0000003` on
      `sha-3f467199c8f97df8e7808e25a8fd8d8a9949fd5d`**, the merge commit of #55,
      with no command typed by hand. The `deploy` job is
      a transcription of steps 12 and 13 of `docs/deploy-azure.md` and nothing
      else: download the bundle from this same run, log in to Azure, read the
      connection string out of the Container Apps secret, apply migrations,
      `containerapp update --image ...:sha-<github.sha>`, then ask Azure what
      image is running and request `/api/transactions`. Migrate first and deploy
      second, which is #37's order.

      **No client secret anywhere.** OIDC federated credentials: an app
      registration with no credential at all, plus a statement in Entra ID that
      a GitHub Actions token for `repo:landcovschi/LandMoney:ref:refs/heads/main`
      may act as it. The client, tenant and subscription ids are repository
      **variables**, not secrets -- they identify an app, they are not a
      password. Creating the registration is the owner's act; step 14 of the
      runbook has the commands, and the subject string is case-sensitive in the
      half (`LandMoney`) that the image name is not.

      **The concurrency block had to change and the obvious fix was wrong.**
      #38 suggests a separate group on the deploy job without
      `cancel-in-progress`; that does not help, because cancellation happens to
      the whole run and takes every job with it, so a push during a deployment
      would still kill it between "migrations applied" and "revision replaced".
      `cancel-in-progress: ${{ github.event_name == 'pull_request' }}` is what
      works, and the queueing comes free: a group without cancellation makes the
      second run wait for the first.

      **Both open questions are now measured, and one of them failed first.**
      A GitHub-hosted runner *does* reach the database through the 0.0.0.0 "all
      Azure services" rule -- `Apply migrations` connected in seven seconds --
      so step 14's temporary firewall rule stays written down and unused. The
      OIDC login did **not** work on the first attempt, for a reason neither #38
      nor the documentation predicts: GitHub sends an immutable subject,
      `repo:<owner>@<ownerId>/<repo>@<repoId>:ref:refs/heads/main`, and
      `use_immutable_subject` reads `false` while the prefix is immutable
      anyway. The error printed the string it wanted, so the fix was a copy.

      **What that failure proved is worth more than the fix.** It stopped at
      step 2 of 6, before the secret was read, before the bundle ran and before
      any revision existed; `build` and `publish` were already green, so the
      image existed and a re-run was the whole remedy. Azure was never touched
      and the previous revision served throughout. The whole job is 71 s once it
      works, of which 10 s is `az extension add` preparing and 19 s is
      `containerapp update`.

**Why not "all of it in GitHub".** GitHub covers two of the three layers: the
pipeline (Actions) and the registry (`ghcr.io`). It does not cover the third.
Pages serves static files -- HTML, CSS, JS -- and this application needs a live
process and a database beside it. Something has to run the container, and that
is what Azure is here for. Worth knowing the split rather than discovering it
halfway through a deployment.

- [x] Real configuration and secrets handling -- no connection string in git
      -- #36, 2026-08-25. Three places a setting can live and the code knows of
      none of them: `appsettings.json` for log levels, user-secrets locally, a
      **Container Apps secret** deployed, reached as
      `ConnectionStrings__Default=secretref:pgconn`. `az containerapp show`
      returns `secretRef` and no value, and `git grep` finds no password
      anywhere. Step 12 of `docs/deploy-azure.md` has every command; `README.md`
      has the short version, because configuration never appears in a diff

      **`ASPNETCORE_ENVIRONMENT=Production` set although it changes nothing** --
      #35 measured the container already logging `Hosting environment:
      Production`, that being the default when the variable is absent. It is
      written down because four middlewares are gated on `!IsDevelopment()` and
      a typo in that variable would turn all four off silently

      **The redirect question turned out to be the wrong question.** Behind the
      ingress `Request.IsHttps` is false, `HstsMiddleware` returns early on
      exactly that, and so **the deployed app was sending no
      `Strict-Transport-Security` header at all** -- measured with `curl -I`,
      not reasoned about. `UseHttpsRedirection` announced its own uselessness
      once per start; HSTS was silent, which is worse. `UseForwardedHeaders`
      with `XForwardedProto` and nothing else fixes both: HSTS emits, and the
      redirect becomes a no-op by design rather than by degradation.
      `XForwardedFor` stays out -- it buys nothing here and is a spoofable
      client IP later

      **The trust lists have to be cleared, and a local test cannot prove it.**
      Checked by mutation, #21's discipline: from `localhost` the mutated build
      is indistinguishable, because loopback is the one proxy the defaults
      trust. Sent to the machine's LAN address instead, the mutation dies. A
      proxy-trust bug cannot be reproduced from the machine running the process.
      Two smaller ones met on the way: `KnownNetworks` is `[Obsolete]` in favour
      of `KnownIPNetworks` (the compiler catching what #22 and #24 caught by
      hand), and `HstsOptions.ExcludedHosts` holds `localhost` by default, which
      made the first measurement read as a failure that was not one

      **Verified in Azure the same day**, on revision `landmoney--0000002`
      running `sha-25720b9`: `strict-transport-security: max-age=2592000` on the
      deployed URL, and `Failed to determine the https port for redirect` gone
      from the startup log after appearing at every start since #23 predicted
      it. That header arriving also settles the one thing the code comment could
      not: **Container Apps' ingress does send `X-Forwarded-Proto`**, which until
      the deployment was reasoning rather than measurement. #38 is what stops the
      image update being a hand step
- [x] Migrations applied as a deployment step, not on application startup
      -- #37, 2026-08-26. `dotnet ef migrations bundle --self-contained
      -r linux-x64`, built by `ci.yml` in the `build` job and uploaded as an
      artifact, run per step 13 of `docs/deploy-azure.md`. Verified against the
      deployed database rather than locally: the artifact from the pull
      request's own CI run applied `TransactionListIndex` to
      `psql-landmoney-pl`, and a second run of the same file answered `No
      migrations were applied`. **No image was deployed and no revision
      created** -- which is the shape of the whole issue: the schema and the
      application move on separate tracks. `script --idempotent`
      lost on who executes the SQL -- `psql` is another dependency and another
      place the connection string arrives; `database update` from the runner lost
      on needing the SDK, the pinned tool and a checkout in a job that only
      deploys

      **The startup answer, sharpened by measuring it.** Concurrency was never
      the sharp end: EF takes `LOCK TABLE "__EFMigrationsHistory" IN ACCESS
      EXCLUSIVE MODE`, so parallel replicas serialise. What decides it is the
      failure shape -- a migration that throws before `app.Run()` is a container
      that exits and restarts for ever, which reads as an application that will
      not start, from a deployment that reported success

      **Fix forward, not restore from backup**, measured on a throwaway database
      rather than argued: a migration is atomic (Postgres has transactional DDL),
      a run of them is not, and because `__EFMigrationsHistory` stays accurate a
      corrected bundle re-run resumes at the migration that failed

      **Still two hand steps, and #38 is what joins them:** downloading the
      artifact and running it, then pointing the container app at the new image.
      Claude does not read the connection string, so the run itself stays the
      owner's until a workflow with an OIDC login does it
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
not made by default later. **Settled 2026-08-25 in #37: the bundle won**, for
the reason the paragraph guessed -- it needs least where it lands. The full
record, including the two traps neither this paragraph nor #37 predicted, is in
`CLAUDE.md`. `dotnet ef database update` from CI is the obvious
one and the worst fit: it needs the SDK, the tools and network reach from the
runner to the database. `dotnet ef migrations script --idempotent` produces SQL
that can be read before it runs, which is what a DBA would ask for.
`dotnet ef migrations bundle` produces a self-contained executable that needs
no SDK where it runs -- the container-shaped answer, and the one expected to
win here.

**On cost, settled in #34 and no longer an open question.** Azure Database for
PostgreSQL is not free, but it is free *here* for twelve months, and the cheap
route this paragraph used to recommend -- Postgres as a container next to the
app -- turned out not to be a route at all once Container Apps storage was
looked at rather than assumed. The number to remember is what happens after the
twelve months: 15-20 USD a month, with `az postgres flexible-server stop` as the
only lever, halting compute billing but not storage, and starting the server
again by itself after 7 days.

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

       **A set landed 2026-08-25 and it is not real spending**, which is why
       this box is still empty. 45 labelled rows and 8 held out, written and
       labelled by Claude on the owner's explicit instruction, the day after
       #44 had refused to invent them. It unblocks everything downstream --
       there is a number, the scorer runs end to end, slice 4 has something to
       be scored against -- and it does not satisfy the line above it, because
       the label distribution was chosen rather than observed, the terse rows
       are one person's idea of how somebody else writes tersely, and every
       description is English. `docs/evals.md` section 5 has the four
       consequences. **This ticks the day real rows replace them**, which is an
       edit to two CSV files and a re-run: nothing in `evals/` knows where a row
       came from

       **A second set landed 2026-08-26 in #47 and this box is still empty.**
       53 labelled rows and 10 held out, and the owner again asked for them to
       be written rather than supplied, so the one defect #47 exists to remove
       survives it. What did change is worth having and is not the same thing:
       the descriptions are typed rather than composed -- lower case, real
       Chisinau merchant names, a typo carried over verbatim from the deployed
       database -- and the currency mix is Moldovan. What did not: the label
       distribution, because the three-rows-per-category floor and a realistic
       shape cannot both hold at 53 rows; and the language, which is now a
       standing decision rather than an oversight after a Russian and Romanian
       first pass was rejected under `CLAUDE.md`'s English rule. That last one
       preserves the single most likely way this baseline reads optimistic.
       `docs/evals.md` section 6 has the full account

       **#62 removed the excuse, on 2026-08-28, and this box is still empty.**
       A CSV of the four columns can now be imported in one request, which is
       what stood between a year of real spending and this list -- typing it.
       What it does not do is label anything: the import stores no category by
       design, and `evals/transactions.csv` needs a fifth column filled in by
       hand. So the remaining work is a bank export converted once, imported,
       and then labelled row by row -- a session's work rather than a feature.
       **This ticks when those rows replace the invented ones**, not when
       something can carry them

       **#63 opened a second route to the same rows, on 2026-08-28, and this box
       is still empty too.** The category column is now a dropdown of the eleven
       with a badge saying where the value came from -- `rules`, `model` or
       `human` -- and a correction stores `category_source = human`. The point is
       not the screen: it is that every other route to labelled data in this
       project is somebody sitting down to do a chore, and this one produces a
       labelled row as a by-product of ordinary use, from the one person who can
       judge it. What it does **not** do is reach `evals/transactions.csv` --
       there is no export, so the rows accumulate in Postgres and getting them
       into the CSV is still a hand job. **This ticks when real labelled rows
       replace the invented ones**, by whichever of the two routes gets there

2. [x] **A rules baseline.** String matching on the description. Score it.
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

       **Re-scored 2026-08-26 against the second set: macro recall 56.1%,
       accuracy 56.6% on 53 rows** -- the number that stands. Down 4.7 points,
       which #47 predicted and called the point of the exercise; read it as the
       same baseline re-measured against harder rows rather than as the baseline
       getting worse, since these are two sets and not two runs. The structure
       is unchanged: **22 of 23 misses are abstentions**, the one confident
       error is `parking fine` -> `transport` again, and `other` still scores
       0%. The new merchant names widen the abstention gap exactly as an English
       substring list would predict

       **Scored 2026-08-25: macro recall 60.8%, accuracy 62.2% on 45 rows.**
       That is the number to beat, and it is measured against the synthetic set
       above rather than against real spending. No rule was edited after seeing
       a miss. What the misses say is about the baseline rather than the data:
       **16 of 17 are abstentions, not confusions** -- `Blood tests`,
       `Oil change`, `Winter boots`, `Dry cleaning` match nothing at all, so
       substring matching fails here by not covering rather than by being wrong.
       The one confident error is `Parking fine` -> `transport`, `parking`
       sitting above `fine`, which is an ordering collision of exactly the kind
       described above and is left alone. And **`other` scores 0% and cannot
       score anything else**: there is no substring meaning "fits nothing", so
       any abstaining substring baseline has a hard ceiling of 90.9% across
       eleven categories, which is 9.1 of the 39.2 points missing here
3. [x] A Python service (FastAPI) that categorises a transaction. Called by the
       .NET app over HTTP, with a timeout and a fallback to the rules -- #39,
       which carries the rules from step 2 inside it so the baseline and the
       service stay the same code and the score keeps meaning something

       **Landed 2026-08-26** as `src/categorizer/`: a `uv` project, FastAPI,
       `POST /categorize` and `GET /health`, 14 tests. `categories.py` and
       `rules.py` moved out of `evals/` without a character changing, and the
       scorer reaches them by putting `src/categorizer/src` on `sys.path` --
       **the baseline re-ran at exactly 60.8% macro recall and 62.2% accuracy**,
       which is the whole point of the move being a move rather than a copy

       The response is `{category, source}` and `source` exists although only
       one producer can write it. That field is the one thing in this issue that
       cannot be added later: a row categorised before the column existed can
       never say where it came from. **The .NET side does not store it yet**, on
       the argument that everything written today is `rules` by construction, so
       the column has to arrive in the same change as the model adapter of step
       4 and before it. Written down under "Open decisions with a deadline" in
       `CLAUDE.md`, because that is the only place a deadline survives

       Abstention crosses the wire as `category: null`, never as the `unknown`
       sentinel -- serving that string would put a twelfth value into the
       application's `category` column, which is the failure the closed
       vocabulary exists to prevent. `score.py` still sees the sentinel, so the
       number is untouched

       **Measured rather than assumed, and it changed the shape of the
       fallback:** stopping the categorizer does not fail fast. `docker compose
       stop categorizer` left the SYN unanswered rather than refused, so the
       .NET client took the *timeout* path, not `HttpRequestException` -- every
       save costs the full timeout while the service is down. That is the
       argument for the 2 seconds in `appsettings.json` being small: it is not
       the latency of a working call, it is the latency of a broken one, on the
       path where the user's transaction is being written

       **The first deploy after this failed**, and the site did not. A `?? throw`
       for `Categorizer:BaseUrl` in `Program.cs` killed `efbundle`, which runs
       that file from a directory with no `appsettings.json`; the job died at
       `Apply migrations`, before `Deploy the revision`, so no revision and no
       schema changed and `landmoney--0000004` served throughout. Fixed by making
       an absent categorizer a legal state -- the same principle as the null
       category, one step earlier -- and by a `ci.yml` step that runs the bundle
       in an empty directory on every pull request, verified against the bundle
       that broke. `CLAUDE.md` has the full record and the general rule: every
       `?? throw` in `Program.cs` is also a deploy-time landmine

       Not in CI, deliberately: nothing builds or tests `src/categorizer/` on a
       pull request yet, so `build` can be green over a broken service. It is
       already a slice 5 item ("Evals run in CI on every PR") and it is the
       natural home for both halves of the Python tree at once

       **Closed on 2026-08-28 in #58**, and both halves did arrive together.
       `ci.yml`'s `build` job now syncs the uv project, runs the service's
       pytest suite, runs the scorer's own tests, and -- the part that is not
       merely running a command -- **compares** the score against
       `evals/baseline.json`, which is the one place the number is asserted.
       Inside `build` rather than in a job of its own, because `build` is the
       required check and a new job protects nothing until the ruleset names
       it

       **It reached Azure on 2026-08-28** in #61, which is later than it sounds:
       between #39 and #61 the service ran in `docker-compose.yml` and nowhere
       else, so the deployed app resolved `Categorizer:BaseUrl` to the
       `appsettings.json` default, found nothing listening on `127.0.0.1:8000`
       inside its own container, and stored **every** transaction with no
       category. Nothing was red the whole time -- the fallback three paragraphs
       up is precisely what hid it, and that is the general lesson worth taking
       out of this project: a dependency the application is designed to run
       without is one whose absence nothing reports

       It is **its own container app with internal ingress**, not a second
       container in the app's revision. The sidecar was cheaper in every
       mechanical way and would have made the cold start below disappear; it
       lost on this being the arrangement worth learning, and on coupling a
       Python release to a .NET revision that signs everybody out when it is
       replaced. `--min-replicas 0` was chosen rather than discovered, so **the
       first save of a session may be stored with no category** -- the app's own
       cold start is 23.3 s and the client gives up after 8 -- and the
       categorizer's own cold start is deliberately recorded as not yet measured
       rather than guessed at

       **`http://` to the internal FQDN was wrong, and the way it was wrong is
       the lesson.** The ingress is created with `allowInsecure: false`, so a
       POST over port 80 is a `301`, `HttpClient` re-issues a 301 as a GET, and
       `/categorize` answers 405 -- another silent null category, arriving
       through the change that exists to end silent null categories. `GET
       /health` over http looks fine throughout, a redirected GET still being a
       GET, so a health-check smoke test could not have caught it. Found by
       probing the internal FQDN from inside a replica with `az containerapp
       exec`, which is the answer to "an internal service cannot be observed"

4. [x] An Anthropic adapter behind a port, plus a fake with canned responses so
       tests never hit the network and never cost money

       **Landed 2026-08-28** in #59, and the half that had to come first did:
       `transactions.category_source` -- the column, the migration and the write
       path -- is in the same change and ahead of the adapter, which closes the
       one entry "Open decisions with a deadline" has ever held. Existing rows
       were **backfilled to `rules`**, argued rather than done quietly, on the
       grounds that it is provably true and not merely defensible. Checked
       against the running database: 2 of 21 rows backfilled, 19 untouched, and
       zero rows where a category exists without a source or the reverse

       **The seam cost nothing, which is what #39 built it for.**
       `AnthropicPredictor` names `Predictor` nowhere -- structural typing, so no
       import and no base class -- and `get_predictor` was the one line that
       changed, exactly as `main.py`'s comment predicted a Protocol would buy

       **The decision the issue actually turned on was the timeout**, and it
       became two: 2 s to connect, 8 s overall. #39's two seconds was chosen
       against the *broken* case, and a model does not fit in it; splitting the
       clocks gives the two failures two budgets rather than making them share
       one. Measured: **142 ms** with the categorizer up and a category stored,
       **2043 ms** with it stopped and no category -- so #39's fast-failure
       property survives unchanged while a live model gets six more seconds

       Two things were wrong before they were measured, and both are in
       `CLAUDE.md`. `BaseUrl` at `http://localhost:8000` made the new budget
       *worse* than the old one -- the dead `::1` attempt ate the whole two
       seconds and a save took the full eight and stored nothing, against 156 ms
       once the key held `127.0.0.1`. And `ConnectTimeout` expires as a
       cancellation rather than as `HttpRequestException`, so the timeout log
       named the eight-second limit for a call that gave up at 2.15 s

       **What is not verified, and it is the honest limit:** no request has ever
       been accepted by the API, because there is no key on this machine. A
       deliberately broken key was run end to end -- a real 401, a logged
       traceback, `200 {"category": null, "source": "model"}`, which is #59's
       acceptance test -- and the request shape was checked against the SDK's own
       types. Whether the model is any good at this is step 5's question and it
       needs a key
5. [x] Run the evals. Did the model beat the baseline? Record the number

       **The scorer half landed 2026-08-28** in #75, and the box stays empty
       because no model call has been made: there is no key on this machine, and
       provisioning one is #76. `score.py --predictor {rules,model}` puts both
       numbers through one scorer over the same rows, and reports the things the
       metric cannot -- the abstention rate, the confident-error count, the full
       confusion matrix and every missed row

       **The seam widened from `str -> str` to `Row -> str`**, against what
       `evals/README.md` argued, because the model is shown the amount and the
       currency and a scorer that could only pass a description would measure a
       different predictor from the one the service runs -- #39's drift, in the
       direction where nothing reports it and the number is merely lower. The
       rules read nothing else, so it could not move the baseline: 56.1% and
       56.6% over 53 rows reproduced across the change, asserted by `--check`
       rather than argued

       **A run that fails prints no number at all**, which is the guard worth
       keeping. `AnthropicPredictor` never raises -- every failure is a null
       category, which is what protects a user's transaction on the .NET side --
       so a failed call is indistinguishable from an abstention, and a keyless
       run would have scored about 0% and read as a model that is bad at the
       job. The scorer counts the adapter's ERROR records and refuses; measured
       with no key, it stops before the first call rather than making 53 doomed
       ones

       **Closed 2026-08-28 in #60, and the answer is 98.9% against 56.1%** --
       +42.8 points of macro recall, +41.5 of accuracy, measured on the same 53
       rows on the same day through one scorer. `claude-opus-5`, `effort=low`,
       prompt `sha256:c8ad9d9fd16f`, and the prompt was **not** edited after
       seeing which rows missed. The full account, including the caveats that
       matter more than the percentage, is section 7 of `docs/evals.md`

       **The failure shape is the interesting half.** One miss in 53 and it is an
       abstention (`fidesco`, a merchant name carrying no signal); **zero
       confident errors**, against the baseline's one. The confusion matrix is a
       clean diagonal plus that single cell -- nothing was confused *for*
       anything. On the .NET side that distinction is the whole game: a null is a
       state the application already handles, a wrong category is stored as if it
       were true

       **`other` went 0/3 to 3/3**, which is #60's specific question answered.
       Section 6 records a 90.9% structural ceiling on any abstaining substring
       baseline, because one category of eleven is unreachable by merchant-name
       matching; 98.9% is above that ceiling, so this is not the same baseline
       tuned further

       **The number is measured, and it is not trustworthy in the way its size
       suggests.** The eval set was written by Claude (#47 asked for real
       spending and is still open) and the predictor scored against it is Claude,
       in English, when real entries would be Russian and Romanian. An LLM
       answering rows an LLM invented is close to grading its own homework, and
       no amount of re-running fixes it -- only #47 does. `evals/holdout.csv` is
       still unlooked-at and is the only instrument left that this section's
       number cannot bias

       Two runs, identical to the row, 114 s and 115 s for 53 calls each -- ~2.1 s
       per call, inside the 6-second timeout the *service* uses, so the number
       describes the deployed configuration rather than a relaxed one. Zero
       failed calls, so the ERROR guard above never fired. `baseline.json` still
       records the rules on purpose: it is what CI asserts, and the model must
       never run on a pull request

**Done when:** the improvement over the baseline can be quoted as a number, and
the thing that produced the number can be shown.

## Slice 5 -- operations

- [x] Redis: identical input must not be billed twice -- #65, 2026-08-29. The
      model's answers are cached in Redis, keyed on the model id, the effort, the
      prompt's digest and **the exact text the model was shown**, with nothing
      normalised -- a fold that existed only in the cache path would be #39's
      caught mutation in a new coat. What a call cost is stored beside the answer,
      and one line per lookup carries the running hit rate. **Only the model path
      has a cache**: measured over HTTP with the rules answering, five requests
      opened zero Redis connections and wrote zero keys. A dead Redis is a model
      call and never a missing category -- and after the first failure it stops
      asking for thirty seconds, which took a stopped container from **1055 ms
      added to every save** down to 531 ms once and 0 ms after
- [x] pgvector: find similar past transactions -- #66, 2026-08-29, and the
      result is that **the eval set ran out of room before retrieval did**. An
      `ExampleStore` port with two implementations, the retrieved rows in the
      user message (not the system prompt, or `cache.py` would replay an answer
      computed from a corpus that has since changed), and one setting that turns
      it off. `holdout.csv` was labelled to give a corpus and an eval that do not
      overlap -- and the model scores **100.0% on it with no retrieval at all**,
      against 98.9% on the 53-row set whose headroom was already +1.1 points
      against a 3-point noise floor. Lexical retrieval holds 100.0%, which is the
      only thing this data can say: the free `--show-examples` run beforehand
      showed the neighbours were mostly noise (`heating` -> `headphones`), so
      holding is a finding about the **prompt** -- the paragraph telling the model
      the rows may be irrelevant, written before the run. The vector arm is
      implemented and unrun, waiting on a `VOYAGE_API_KEY`. Section 8 of
      `docs/evals.md` is the account, and its conclusion is that #47 is now the
      single most valuable open item in the project
- [x] Token and cost accounting per request -- #64, 2026-08-29. The Python
      adapter logs one `model_call` line per call carrying the outcome, the
      elapsed time, the input and output tokens off `message.usage`, and the
      cost when a price is configured. **No price is written into the code**:
      a rate changes without this repository noticing, and a stale figure in a
      log is worse than an absent one because it is believed. Tokens are the
      fact and the money is the multiplication
- [x] What the categorizer is actually doing, in production -- #64,
      2026-08-29. Nine named outcomes recorded on every exit of
      `CategorizerClient`, a `Meter` nothing reads yet, and one summary line
      per window in which anything happened. Measured against the running
      stack: a stopped categorizer and three saves reads as **3 timed out, 0
      unreachable**, p95 2051ms -- which is the number that could not be
      stated at all the day before. A metrics endpoint is deliberately still
      open
- [x] The categorizer is visible while a transaction is typed -- #67,
      2026-08-29. `POST /api/transactions/category-suggestion` answers what the
      categorizer would say for a description that is not saved yet, and a badge
      under the description field shows it 400 ms after the typing stops. Not on
      the roadmap before this: the AI half worked and said so only in a table
      row, after the fact.

      **A POST that writes nothing**, because a GET would put the user's
      spending in a query string and into every access log on the way, and would
      carry the `SameSite=Lax` cookie on a top-level navigation -- so the two
      CSRF locks #52 recorded would both be gone. The browser cannot call the
      categorizer itself: it is internal-only (#61) and unauthenticated

      **The distinction the save path never needed.** An abstention and a dead
      service are both `null` there and are treated the same, correctly. Here
      "no idea" has to be **visible** -- it is a normal answer on roughly a
      third of the labelled set -- and a categorizer that is not running has to
      be **invisible**, because nothing the person typing can do would help.
      `CategorizerAnswer` carries who answered beside what they said, and the
      source is what says something answered at all

      **The save asks again rather than trusting the browser.** One call and a
      guarantee that the screen and the row agree was the alternative, and it
      lost on provenance: a client that can send a category can send a source,
      which is the hole #59 closed

      **The calls are counted by what asked for them**, which keeps #64's
      numbers meaning what they say -- from here on the previews are the
      majority and against the model each one is a charge

      **First endpoint in the application testable end to end**, because it
      touches no database: 34 new tests, seven mutations, all caught. The React
      half has no test framework to check it with, and says so
- [ ] Graceful degradation: the AI service is down, the app still works
- [x] Evals run in CI on every PR -- #58, 2026-08-28. `python evals/score.py
      --check` compares the run against `evals/baseline.json` and exits 2 when
      the number moved, so a step that is green means the baseline is the
      recorded one rather than merely that a number was printed. The
      categorizer's own tests came with it: nothing had touched the Python tree
      on a pull request until then

## Deliberately not doing

Recorded so they do not creep back in:

- Roles and an admin panel -- until slice 4 is running

  **Authentication came off this list on 2026-08-26**, in #52, and it came off
  early rather than on schedule. The line read "until slice 4 is running" and
  slice 4 was not: steps 4 and 5 (#59, #60) were open, so there was no number to
  quote. The owner was shown that and asked for it anyway. Recorded here in the
  same words as in `CLAUDE.md`, because an exception nobody wrote down is how a
  list like this stops meaning anything

  What landed: **ASP.NET Core Identity** -- a username, a password and a login
  form in the client -- with registration gated on an invite code, no email and
  no password reset. Every transaction is owned by the account that entered it,
  and a global query filter makes forgetting to scope a query something a future
  endpoint cannot do. 35 new tests, still with no Postgres and no network. The
  decision is in `CLAUDE.md`; the commands are step 15 of `docs/deploy-azure.md`

  **It was OpenID Connect for one day first**, wired against a configurable
  provider on 2026-08-26 because that is what #52 recommends, and replaced on
  2026-08-27 because the owner wanted a form and a password rather than a
  redirect to somebody else's page. The ownership half -- column, filter,
  stamping, index, tests -- survived the swap untouched, which is the clearest
  evidence available that `ICurrentUser` was cut in the right place

  **The bug worth carrying forward, and it was caught by running rather than
  reading.** `AppDbContext` captured the owner in its constructor, which is wrong
  once Identity is in the pipeline: the cookie handler resolves the EF store
  during `UseAuthentication`, so the context is built while the request is still
  anonymous, and a scoped service keeps that null for the whole request. Every
  read answered `WHERE owner_id IS NULL` and every write stamped null, so two
  accounts saw one shared list with no error anywhere and every unit test green.
  A filter that fails to nothing is loud; that one failed to everything
- Multi-currency conversion. Amounts keep their currency; no implicit maths
- Bank integrations. Manual entry and CSV import are enough to learn from
- Anything resembling investment or financial advice. Categorising past
  spending is a data problem; telling someone what to do with their money is
  not what this project is
