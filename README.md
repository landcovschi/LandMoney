# LandMoney

Personal spending tracker. An ASP.NET Core Web API on top of Postgres with a
React and TypeScript client, deployed to Azure, and a Python service for
transaction categorisation arriving later. The client is built into the API's
`wwwroot` and served by it: one origin, one image, one deployment.

It is a real application -- meant to be used, not demonstrated -- but the
reason it exists is a move from .NET development into AI engineering. See
`docs/roadmap.md` for the plan and for what went wrong in the previous attempt.

## Requirements

- .NET 10 SDK
- Node 24 LTS, for the client
- Docker Desktop
- Azure CLI (from slice 3 onwards)

## Getting started

```powershell
copy .env.example .env
docker compose up -d
dotnet tool restore
dotnet ef database update --project src\LandMoney.Web
```

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

## Where the configuration lives

There is no connection string in this repository and there is not meant to be
one. The same key is filled from a different place depending on where the
application is running, and the application never asks which:

| Running                     | `ConnectionStrings:Default` comes from                     |
| --------------------------- | ---------------------------------------------------------- |
| On this machine             | User-secrets (`dotnet user-secrets set`)                    |
| In the deployed container   | A **Container Apps secret**, referenced by an env var       |

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
