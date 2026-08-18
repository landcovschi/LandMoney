# LandMoney

Personal spending tracker. ASP.NET MVC on top of Postgres, deployed to Azure,
with a Python service for transaction categorisation arriving later.

It is a real application -- meant to be used, not demonstrated -- but the
reason it exists is a move from .NET development into AI engineering. See
`docs/roadmap.md` for the plan and for what went wrong in the previous attempt.

## Requirements

- .NET 10 SDK
- Docker Desktop
- Azure CLI (from slice 3 onwards)

## Getting started

```powershell
copy .env.example .env
docker compose up -d
```

The rest arrives with slice 1.

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
