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

- **UI: ASP.NET MVC / Razor Pages.** Blazor Server was the alternative and
  lost on debuggability: plain request-response is easier to reason about
  behind a reverse proxy, and there is no SignalR connection to keep alive.
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
