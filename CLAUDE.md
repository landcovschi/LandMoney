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
- .NET 10 (LTS). Python >= 3.12 with `uv` when the categorizer arrives.
- Money is `decimal`, never `double` or `float`. Amounts are stored with their
  currency; there is no implicit conversion anywhere.
- Dates and times are stored in UTC, converted only for display.
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

  **The client is served by the .NET app as static files**, built in CI into
  `wwwroot`, one image, one deployment. A separate nginx container was the
  alternative and lost for now on moving parts: it adds CORS, a second image
  and a second thing to deploy for no benefit at this size. It becomes the
  right answer once the Python service arrives and there are several
  containers anyway.

- **Kubernetes: considered on 2026-08-07 and deliberately not adopted.** For
  two containers it is weeks of manifests, ingress and secrets that produce
  nothing a user can see, and a managed cluster costs real money for node
  VMs. The skill is worth having, and when it is wanted the honest way to
  learn it is a local cluster (`kind`, or the one built into Docker Desktop),
  which speaks the same API for free. Container Apps stays.
- **Build and deploy: GitHub Actions.** Public repository, so the minutes are
  free.
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
- **No ruleset on `main` yet, deliberately.** Required status checks are worth
  having, but there is no CI workflow to require until slice 2. An empty
  requirement would only block merging without verifying anything.
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

- **Which day does a transaction belong to?** `timestamptz` stores an instant,
  and an instant only becomes a date once a timezone is applied. A purchase at
  01:00 local in UTC+3 is stored at 22:00 UTC **on the previous day**. Group by
  date in UTC and it lands in the wrong day; group by the viewer's zone and the
  answer changes when the viewer travels. Three ways out, none wrong: keep the
  instant and fix one reporting timezone; store the original offset in a second
  column; or make `OccurredAt` a plain date on the grounds that nobody types
  the minute they paid for coffee. **Decide before #6 groups anything by day.**

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
