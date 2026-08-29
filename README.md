# LandMoney

Personal spending tracker. An ASP.NET Core Web API on top of Postgres with a
React and TypeScript client, deployed to Azure, and a Python service that
suggests a category for each transaction. The client is built into the API's
`wwwroot` and served by it: one origin, one image, one deployment.

It is a real application -- meant to be used, not demonstrated -- but the
reason it exists is a move from .NET development into AI engineering. See
`docs/roadmap.md` for the plan and for what went wrong in the previous attempt.

## Requirements

- .NET 10 SDK
- Node 24 LTS, for the client
- Docker Desktop
- Azure CLI (from slice 3 onwards)
- `uv`, for the categorizer -- and nothing else Python. It fetches the
  interpreter named in `src/categorizer/.python-version` itself, so a machine
  with no Python at all needs no second install

## Getting started

```powershell
copy .env.example .env
docker compose up -d
dotnet tool restore
dotnet ef database update --project src\LandMoney.Web
```

`docker compose up -d` brings up **Postgres and the categorizer** -- the two
things the application talks to. The app itself is not in that set on purpose:
it is run from the host, where the debugger and the fast rebuild are. See
"Running the whole stack in containers" below for the other way.

The connection string lives in user-secrets and is not in any committed file;
`Program.cs` fails at startup with the command to set it if it is missing.

Then build the client once and run the app on its own:

```powershell
npm ci --prefix src\landmoney.client
npm run build --prefix src\landmoney.client
dotnet run --project src\LandMoney.Web
```

`http://localhost:5150` is the whole application. `npm run build` writes into
`src\LandMoney.Web\wwwroot`, which is build output and git-ignored -- a clone
that skips it gets a 404 at `/`, because there is genuinely nothing to serve.

For working on the client itself, run the Vite dev server beside the API and use
its port instead; `src/landmoney.client/README.md` has the details.

## The categorizer

A FastAPI service holding the rules baseline of #25 -- one endpoint in, one
category out, and **no model call, by rule**: it exists to be the number a model
has to beat. `src/categorizer/README.md` is how to run and test it on its own.

Nothing has to be done to use it. `docker compose up -d` starts it and publishes
it on `127.0.0.1:8000`, which is the default in `appsettings.json`, so a
transaction posted to a host-run API comes back categorised.

If it is not running, transactions are still saved -- with `category: null`.
That is the whole design rather than a graceful accident: the transaction is the
user's data and the category is a guess about it, so the guess is never allowed
to cost the row. Expect each save to take the full two-second timeout while the
service is down; a stopped container does not refuse connections, it swallows
them.

## Running the whole stack in containers

```powershell
docker compose --profile full up -d --build
```

That adds the .NET app, on `http://127.0.0.1:8080`, and it is the only
arrangement in which the app reaches the categorizer **by service name**
(`http://categorizer:8000`) rather than through a published port -- inside the
compose network `localhost` is the app's own container, where nothing is
listening. It is worth doing after changing anything about the image or that
hop, and it is not the everyday loop: the default `up` does not build the .NET
image at all.

The schema is still the host's job. The app does not migrate itself (see below),
and compose has no step that does, so `dotnet ef database update` from the host
is what puts it there -- it reaches the same database over the published 5433.

## Where the configuration lives

There is no connection string in this repository and there is not meant to be
one. The same key is filled from a different place depending on where the
application is running, and the application never asks which:

| Running                     | `ConnectionStrings:Default` comes from                     |
| --------------------------- | ---------------------------------------------------------- |
| On this machine             | User-secrets (`dotnet user-secrets set`)                    |
| In the `full` compose profile | `docker-compose.yml`, built from the same `.env` Postgres uses |
| In the deployed container   | A **Container Apps secret**, referenced by an env var       |

`Categorizer:BaseUrl` is the opposite case and is worth the contrast: it names a
service and carries no credential, so it sits in the committed `appsettings.json`
rather than in a secret store. A value being configuration is not the same as a
value being a secret.

It still takes three different values, one per arrangement, and only the first
is the committed default:

| Running                       | `Categorizer:BaseUrl`                                    |
| ----------------------------- | -------------------------------------------------------- |
| On this machine               | `http://127.0.0.1:8000` from `appsettings.json` -- and `127.0.0.1` rather than `localhost` on purpose, see the comment there |
| In the `full` compose profile | `http://categorizer:8000`, the service name on the compose network |
| In the deployed container     | `https://` plus the categorizer app's **internal FQDN**, set as an env var in Azure -- and `https`, because the ingress answers a POST over http with a 301 that turns it into a GET |

The last one has been true only since #61; before it the deployed app fell back
to the first row, found nothing listening inside its own container, and stored
every transaction with no category. Step 16 of `docs/deploy-azure.md` is the
setting, and `ci.yml` asserts it on every deployment -- the alternative is a
failure that shows up as a feature quietly not existing.

That failure is also why there are numbers now (#64). Every call to the
categorizer records one of nine outcomes, and once a minute -- and only when
something happened -- the application writes one line saying how many of each and
what the p95 was. `Categorizer:SummaryIntervalSeconds` is the interval and `0`
turns the line off. Outside Development the log is JSON, one row per entry, so
those fields can be queried rather than searched: step 17 of
`docs/deploy-azure.md` has the query.

The money is counted on the other side of the wire, in the Python service, which
is the only process that can see it: `CATEGORIZER_PRICE_INPUT_PER_MTOK` and
`CATEGORIZER_PRICE_OUTPUT_PER_MTOK` in `.env`, both or neither, and with neither
set the per-call line still reports the tokens. There is no price in the code on
purpose -- a rate moves without this repository noticing, and a stale figure in a
log is worse than a missing one.

**Signing in needs one setting, and it is a secret.** Accounts live in this
application's own database -- ASP.NET Core Identity, a username and a password,
and a login form in the client. There is no identity provider and nothing to
register with. What is configured is `Authentication:InviteCode`: the code a new
account has to quote, without which registration is refused. Deployed it is a
Container Apps secret, referenced the way the connection string is; step 15 of
`docs/deploy-azure.md` has the commands.

**Locally there is no code and none is needed.** With `Authentication:InviteCode`
empty, registration on a developer machine asks for none, so `dotnet run` plus one
form is a working account. That happens in the `Development` environment and
nowhere else: anywhere else, an empty code means nobody new may register at all.
It fails closed, and it still starts -- `efbundle` runs `Program.cs` with no
configuration at all, and #57 is what a startup throw on that path costs.

**There is no password reset**, on purpose: it would mean an email provider, an
API key and a sender domain. A forgotten password is an administrative act, and
step 15 has it.

`Program.cs` reads `GetConnectionString("Default")` and throws at startup naming
the user-secrets command if it is missing. That message is right on a developer
machine and misleading everywhere else -- if it ever appears in the deployed
logs, the cause is the environment variable, not a user secret.

**The deployed value is set as a secret and referenced, never pasted.** The
environment variable is `ConnectionStrings__Default` -- **two** underscores,
which the environment variable provider maps to `ConnectionStrings:Default`; one
underscore makes a key nobody reads. Its value is `secretref:pgconn`, so the
connection string is not returned by `az containerapp show`, by the portal, or
in the revision template:

```powershell
az containerapp show -g rg-landmoney -n landmoney --query "properties.template.containers[0].env" -o json
```

```json
[
  { "name": "ConnectionStrings__Default", "secretRef": "pgconn", "value": "" },
  { "name": "ASPNETCORE_ENVIRONMENT", "value": "Production" }
]
```

The empty `value` beside the `secretRef` is the point: the field is there and
holds nothing, because what fills it is resolved when the container starts and
never comes back out.

`ASPNETCORE_ENVIRONMENT` is set explicitly although Production is already the
default with nothing set. It is what gates `UseExceptionHandler`, `UseHsts`,
`UseHttpsRedirection` and `UseForwardedHeaders` in `Program.cs`, and a value
that important should be readable rather than assumed.

**Every command that produced the deployed configuration is in
`docs/deploy-azure.md`**, step 12, including the trap that a changed secret does
not reach a running revision. This section exists because configuration is
invisible in a diff: nothing in a pull request would ever show it, so it has to
be written down somewhere a person will look.

## How the schema gets there

The application does **not** migrate itself at startup: there is no
`Database.Migrate()` anywhere, and `Program.cs` explains why beside the
`AddDbContext` call. A migration that throws in there would leave a container
that exits and restarts for ever -- an application that will not start, from a
deployment that reported success.

Instead `ci.yml` builds `efbundle`, a single self-contained linux-x64 executable
holding the migrations, and uploads it as an artifact of every run. Deploying is
then: run the bundle against the database, then point the container app at the
new image. That order, and only that order, is safe while migrations only add.

Locally nothing changes -- `dotnet ef database update` is still the answer on a
developer machine, and the local Postgres is a container.

**Step 13 of `docs/deploy-azure.md` has every command**, including where the
connection string comes from (the same Container Apps secret the app uses, read
back rather than copied) and what to do when a migration fails halfway.

## Looking into the database

The schema and the rows are easiest to inspect from a desktop client. DBeaver
Community is free and speaks Postgres:

```powershell
winget install DBeaver.DBeaver.Community
```

The id has to be the full one. `dbeaver.dbeaver` is a product code that
`winget search` matches on and `winget install` does not, so the short form
answers `No package found matching input criteria` while the tool clearly knows
about the package.

`New Database Connection` -> `PostgreSQL`, then take every value from `.env`
(or from the defaults in `docker-compose.yml` if there is no `.env` yet):

| Field    | Value                                       |
|----------|---------------------------------------------|
| Host     | `127.0.0.1` -- **not** `localhost`, see the note in `.env.example` |
| Port     | `POSTGRES_PORT`, 5433 by default            |
| Database | `POSTGRES_DB`                               |
| User     | `POSTGRES_USER`                             |
| Password | `POSTGRES_PASSWORD`                         |

The container has to be up first (`docker compose up -d`), which means Docker
Desktop has to be running -- it usually is not.

A client is a convenience, not a dependency: nothing in the build, the tests or
the deployment knows about it, and `docker compose exec postgres psql` remains
the answer when only one query is needed.
