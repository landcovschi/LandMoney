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
