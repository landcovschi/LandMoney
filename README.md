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
