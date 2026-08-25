# Deploying to Azure, by hand

The commands that put this application into Azure the first time, in the order
they worked -- #35. Written while running them, not from a tutorial, because
three of them turned out to differ from what the documentation says.

**Who runs these.** The owner. `az login`, the subscription, the payment method
and the database password are never typed by Claude and never appear here with a
real value. Everything in `<angle brackets>` is a placeholder.

**What this produces:** one resource group holding a Container Apps environment,
a container app pulling from `ghcr.io`, and an Azure Database for PostgreSQL
Flexible Server. The reasoning behind each of those choices is in `CLAUDE.md`
under "The stack, and what was rejected"; this file is only the how.

**The automated version of this is #38.** These commands are what it
transcribes, which is the reason for doing it by hand first: a deployment
written straight into a workflow fails inside a runner where nothing can be
inspected.

## Names, decided once

| What            | Name                | Note                                                    |
| --------------- | ------------------- | ------------------------------------------------------- |
| Resource group  | `rg-landmoney`      | Deleting it deletes everything, in one line             |
| Region          | `polandcentral`     | Forced -- see step 3                                    |
| Postgres server | `psql-landmoney-pl` | Globally unique; it becomes a DNS name                  |
| Postgres admin  | `landmoney`         | `admin`, `root`, `public`, `pg_*` are refused           |
| Database        | `landmoney`         | Same name as local, so only host and credentials differ |
| Environment     | `cae-landmoney`     |                                                         |
| Container app   | `landmoney`         | First label of the URL                                  |

## Step 0 -- the CLI, and one bug in it

```
winget install --exact --id Microsoft.AzureCLI
```

Then **open a new terminal**: PATH is read when a process starts, so no existing
shell can see it. `CLAUDE.md` records the same lesson for Node.

```
az login
```

**This crashes on a fresh account**, and the traceback is not about the account:

```
AttributeError: 'NoneType' object has no attribute 'get'
  ... _subscription_selector.py, line 98, in _get_tenant_string
```

It is the interactive tenant/subscription picker failing to render a row whose
tenant object came back empty. The picker is a feature that can be turned off,
which is the fix rather than a workaround:

```
az config set core.login_experience_v2=off
```

`az login` then prints the subscriptions as JSON and selects the first. Confirm
before creating anything -- a resource group created against a silently
defaulted subscription is found weeks later, in a bill:

```
az account list --all --output table
```

The other message worth recognising here is `No subscriptions found for <you>`,
which means the tenant that was enumerated holds none. Signing up with a Gmail
address creates a new "Default Directory" tenant, so the usual causes are that
the subscription is still being provisioned (five to fifteen minutes, and the
CLI caches -- `az account list --all --refresh`), or that a different tenant was
picked and `az login --tenant <tenant-id>` is needed.

## Step 1 -- resource providers

A new subscription has almost nothing registered, and this is where a first
deployment usually stops. The failure arrives later, while creating the
environment, as `MissingSubscriptionRegistration: The subscription is not
registered to use namespace 'Microsoft.App'` -- which reads like a permissions
problem and is not.

**`az provider register` takes one `--namespace`.** Repeating the flag registers
the last one and drops the rest silently, so this is three commands:

```
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.DBforPostgreSQL
```

Each returns immediately and registers over the next few minutes:

```
az provider list --query "[].[namespace,registrationState]" -o tsv
```

`Microsoft.OperationalInsights` is in the list because a Container Apps
environment creates a Log Analytics workspace for itself. Its absence fails
while creating the environment, naming a namespace nobody typed.

## Step 2 -- the resource group

```
az group create --name rg-landmoney --location polandcentral --output table
```

## Step 3 -- check the region before creating anything in it

This step exists because skipping it cost a four-minute failure. **A new
subscription is restricted from provisioning Postgres in the popular regions**,
and `list-skus` says so before `create` does:

```
az postgres flexible-server list-skus -l westeurope -o json
```

```
"reason": "Subscriptions are restricted from provisioning in this region.
           Please choose a different region."
"supportedServerEditions": []
{ "name": "OfferRestricted", "status": "Enabled" }
```

Measured on 2026-08-25 for this subscription: **West Europe and Germany West
Central are restricted; Poland Central, North Europe, Sweden Central, France
Central, Italy North, Norway East, Switzerland North, UK South and Spain
Central are not.** That is a property of the subscription and the day, not a
fact about Azure -- re-run it rather than trusting the list.

Poland Central was chosen: it is the closest region to Chisinau, it is not
restricted, it carries PostgreSQL 17 and `Standard_B1ms`, and it hosts Container
Apps environments -- which has to be true in the *same* region and is a separate
question:

```
az provider show -n Microsoft.App --query "resourceTypes[?resourceType=='managedEnvironments'].locations[]" -o tsv
```

West Europe was the first choice, on breadth of service availability. It lost to
being unavailable, which is not an argument that can be had.

A resource group cannot be moved, so a region decided after step 2 means
deleting it -- free, and only while it is still empty.

## Step 4 -- the Postgres server

The password is invented here and is needed twice more (the migration, and the
Container Apps secret). `Read-Host` input is not recorded by PSReadLine, so this
keeps it out of the shell history, out of this file, and out of the chat:

```
$pgPassword = Read-Host -AsSecureString 'New Postgres admin password'; $pgPlain = [System.Net.NetworkCredential]::new('', $pgPassword).Password
```

8-128 characters, three of the four character classes, and it may not contain
the admin username. Azure cannot show it back afterwards -- the only recovery is
`az postgres flexible-server update --admin-password`. Save it in a password
manager before pressing Enter, and keep the window open: `$pgPlain` dies with it.

```
az postgres flexible-server create --resource-group rg-landmoney --name psql-landmoney-pl --location polandcentral --tier Burstable --sku-name Standard_B1ms --storage-size 32 --storage-auto-grow Disabled --version 17 --public-access None --admin-user landmoney --admin-password $pgPlain --yes
```

Three to ten minutes. The real state, rather than the spinner's opinion, comes
from a second terminal and answers `Provisioning` then `Ready`:

```
az postgres flexible-server list -g rg-landmoney --query "[].[name,state]" -o tsv
```

**`--public-access` is the flag to get wrong**, because none of its values read
like what they do. Measured on this server rather than taken from the help:

- **`None` disables public networking altogether.** It does not mean "public,
  with no rules yet", which is what it sounds like and what was assumed here.
  `az postgres flexible-server show --query network` answers
  `"publicNetworkAccess": "Disabled"`, and the next command then fails with
  **`Firewall rule operations are not supported for a server without public
  access enabled`** -- a message about firewalls, for a cause four commands
  earlier.
- **`All` opens the server to the entire internet.** One word from `None`, and
  the opposite end of the range.
- **Omitting the flag is not neutral**: the default adds a firewall rule for
  whatever IP the machine happens to have, which is a rule nobody wrote down.

The recovery costs nothing, and is the reason this is a correction rather than a
recreated server -- the VNet fields are null, so nothing is locked in:

```
az postgres flexible-server update --resource-group rg-landmoney --name psql-landmoney-pl --public-access Enabled
```

The `create` above is left spelled the way it was actually run, because this file
claims to be the order that worked and a silently corrected command is a claim
nobody checked. **A second attempt should pass `--public-access Enabled` at
create time and skip this update** -- which is untested here, and is therefore a
note rather than the command.

**`--storage-size 32`** is the free allowance exactly, and also the tier minimum.
**`--storage-auto-grow Disabled`** is already this CLI version's default; it
stays written because a default that protects the free tier is not one to depend
on silently. Auto-grow would expand past 32 GB and begin billing with nothing
asking. The cost of disabling it is that a full disk stops writes, which at 32 GB
and a few transactions a week is not the failure that will happen here.

**`--version 17`** matches the local `pgvector/pgvector:pg17` of
`docker-compose.yml`, per #34.

### Three flags this CLI version has moved

Written down because all three are still in the documentation and in every blog
post, and each fails as an argument error that reads like a broken command:

- **`--high-availability Disabled`** -> `unrecognized arguments`. Replaced by
  `--zonal-resiliency`. Burstable cannot do zone redundancy at all, so the flag
  is dropped rather than translated.
- **`--database-name landmoney`** -> "can only be used when `--node-count` is
  present, as it only applies to elastic clusters". The database is now its own
  step.
- **`--public-access 0.0.0.0`** as a *create* argument. This version documents
  `Disabled`, `Enabled`, `All`, `None`, an IP, or a range. `0.0.0.0` keeps its
  "all Azure-internal addresses" meaning on `firewall-rule create`, where the
  help states it outright -- which is where step 5 uses it.

## Step 5 -- who may connect

Two rules, and the first one is the compromise #34 named. A Container App on the
Consumption workload profile has **no stable outbound IP**, so there is no
address to pin a rule to:

```
az postgres flexible-server firewall-rule create --resource-group rg-landmoney --server-name psql-landmoney-pl --name AllowAllAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
```

`0.0.0.0` as both start and end is Azure's documented spelling of "all
Azure-internal addresses" -- the CLI help says so in as many words. What it
actually admits is **every Azure tenant's resources, not only this
subscription**. The guards are the password and enforced TLS. The alternative
was VNet integration with a delegated subnet, which #34 describes and which lost
here on a cost that is easy to overlook: with private access, this machine
cannot reach the database at all, so the migration below would need a jumpbox or
a VPN. That is the answer to revisit if this ever holds data worth more than a
personal spending log.

The second rule is this machine, for the migration:

```
$myIp = (Invoke-RestMethod 'https://api.ipify.org?format=json').ip; az postgres flexible-server firewall-rule create --resource-group rg-landmoney --server-name psql-landmoney-pl --name laptop --start-ip-address $myIp --end-ip-address $myIp
```

A residential ISP reassigns that address on reconnect, and a stale rule presents
as a **timeout rather than a rejection** -- worth recognising, because a hanging
`dotnet ef` reads like a broken migration.

## Step 6 -- the database

The server creates no application database of its own. `db list` on a fresh
server answers `azure_maintenance`, `postgres`, `azure_sys` and nothing else, so
this is not adding one beside a default:

```
az postgres flexible-server db create --resource-group rg-landmoney --server-name psql-landmoney-pl --name landmoney
```

## Step 7 -- pgvector, allowlisted early

Nothing uses it until slice 5. It is set now because the alternative is
discovering it during that work:

```
az postgres flexible-server parameter set --resource-group rg-landmoney --server-name psql-landmoney-pl --name azure.extensions --value vector
```

**The name is `vector`, not `pgvector`** -- #34 predicted this and the allowed
values list confirms it; `pgvector` is what the community calls it, `vector` is
what the binary and `CREATE EXTENSION` are called. The open question #34 left was
whether this needs a restart. It does not:

```
az postgres flexible-server parameter show -g rg-landmoney -s psql-landmoney-pl -n azure.extensions --query "[value,isConfigPendingRestart]" -o tsv
vector    false
```

## Step 8 -- the schema, by hand, once

**This step is the throwaway that #37 exists to replace.** `dotnet ef database
update` from a developer machine is the first of the three mechanisms the
roadmap lists and the one it expects to lose: it needs the SDK, the tools, and a
firewall rule for whoever runs it. It is here only because #35's acceptance test
needs `/api/transactions` to answer with something.

The password comes from the variable, so the line typed contains only its name
and the shell history records only that:

```
$env:ConnectionStrings__Default = "Host=psql-landmoney-pl.postgres.database.azure.com;Port=5432;Database=landmoney;Username=landmoney;Password=$pgPlain;SSL Mode=Require;Timeout=15;Command Timeout=30"
dotnet tool restore
dotnet ef database update --project src/LandMoney.Web
```

`Timeout` and `Command Timeout` because `CLAUDE.md` requires every network
client to carry them: an outage should be an error, not a hang.

**`SSL Mode=Require` needs no `Trust Server Certificate=true`,** measured rather
than assumed. Npgsql 8 changed `Require` from "encrypt without checking" to
"encrypt and validate", so this was a real question; Azure's certificate chains
to DigiCert Global Root G2, which Windows and the Debian-based runtime image both
trust. So nothing in this connection string tells a client to skip verification,
which is the outcome to protect if it is ever edited.

Two things to read in the output:

- **The `fail:` line at the top is expected.** EF probes for
  `__EFMigrationsHistory` before it can know whether the database was ever
  migrated; on a fresh database that `SELECT` throws and EF proceeds. It is also
  the proof this reached Azure rather than the local container, where the table
  exists and the query succeeds.
- **It must say `Applying migration '20260818192031_InitialCreate'`.** `No
  migrations were applied` means the environment variable never arrived and it
  silently used the local Postgres, where the migration is already applied.
  Success-shaped, and wrong.

## Step 9 -- the Container Apps environment

The `containerapp` commands live in an extension. Installing it explicitly keeps
an interactive prompt out of the middle of a runbook:

```
az extension add --name containerapp --upgrade
az containerapp env create --resource-group rg-landmoney --name cae-landmoney --location polandcentral
```

Two to five minutes. This is what creates the Log Analytics workspace -- named
something like `workspace-rglandmoney3UEt` -- and therefore what would have
failed had `Microsoft.OperationalInsights` not been registered in step 1, naming
a namespace nobody typed. That workspace is also the one resource here that
nobody asked for and the likeliest source of an unexpected charge, since its
ingestion is not covered by the twelve-month Postgres allowance.

## Step 10 -- the container app

```
$pgConn = "Host=psql-landmoney-pl.postgres.database.azure.com;Port=5432;Database=landmoney;Username=landmoney;Password=$pgPlain;SslMode=Require;Timeout=15;CommandTimeout=30"
```

**Note the spelling change from step 8**: `SslMode` and `CommandTimeout` rather
than `SSL Mode` and `Command Timeout`. Npgsql normalises keywords by stripping
spaces, so these are the same keys -- and the space-free form survives PowerShell
handing the value to a `.cmd` shim, which is where a quoted argument containing
spaces is most likely to be mangled.

```
az containerapp create --resource-group rg-landmoney --name landmoney --environment cae-landmoney --image ghcr.io/landcovschi/landmoney:sha-<40 characters> --target-port 8080 --ingress external --min-replicas 0 --max-replicas 1 --cpu 0.5 --memory 1.0Gi --secrets "pgconn=$pgConn" --env-vars "ConnectionStrings__Default=secretref:pgconn"
```

- **`--target-port 8080`.** #35's first trap. The image listens on 8080 because
  the aspnet base image sets `ASPNETCORE_HTTP_PORTS=8080`, which it does because
  ports below 1024 need a capability the non-root user does not have. Container
  Apps defaults its target port to 80, and the mismatch **does not fail loudly**:
  the revision provisions successfully and then fails every health probe.
- **`--min-replicas 0`.** The reason this service was chosen over App Service,
  and the reason for the cold start measured below.
- **`--max-replicas 1`.** Not required. With `Database.Migrate()` correctly
  absent there is nothing that breaks under concurrency; one replica keeps the
  free grant and the log volume predictable while this is being learned.
- **The SHA tag, never `latest`.** #35's third trap: a revision pinned to
  `latest` cannot answer what it is running, and rolling back means working out
  what `latest` used to be.
- **No registry credentials.** The `ghcr.io` package is public, which #24 could
  not verify until it had run on `main`. Verified here, anonymously, without
  logging in:

  ```
  curl -s "https://ghcr.io/token?scope=repository:landcovschi/landmoney:pull&service=ghcr.io"
  curl -s -H "Authorization: Bearer <token>" "https://ghcr.io/v2/landcovschi/landmoney/tags/list"
  ```

  If the package were still private the revision would fail to start with
  `UNAUTHORIZED`, and the fix is the package settings on GitHub, not a flag here.
- **`--secrets` plus `secretref:`** rather than the connection string in
  `--env-vars` directly. Strictly #36's item, taken early here because the wrong
  way round leaves the password readable in `az containerapp show` until it is
  fixed. What is left for #36: the `ASPNETCORE_ENVIRONMENT` question, the
  `ForwardedHeaders` decision, and the `README` note.

## Step 11 -- what "it works" means, checked

```
az containerapp show -g rg-landmoney -n landmoney --query "{state:properties.provisioningState,fqdn:properties.configuration.ingress.fqdn,targetPort:properties.configuration.ingress.targetPort,image:properties.template.containers[0].image,env:properties.template.containers[0].env}" -o json
```

The URL is `https://landmoney.redstone-8c11320c.polandcentral.azurecontainerapps.io`.
The random middle label is assigned per environment and cannot be chosen.

| Check                                   | Result                                    |
| --------------------------------------- | ----------------------------------------- |
| `GET /`                                 | 200, `Cache-Control: no-cache`            |
| `GET /api/transactions`                 | 200, `[]` before any data                 |
| A transaction added through the form    | appears in the list                       |
| `az containerapp revision restart`      | `"Restart succeeded"`                     |
| `GET /api/transactions` after restart   | both rows still there                     |

The `no-cache` on `/` is worth checking rather than assuming: `MapFallbackToFile`
builds its own `StaticFileMiddleware`, so `/` and `/index.html` disagree unless
both are given the same options -- #20's fix, confirmed here on the deployed app.

The restart test is the one that would have failed under the option #34
rejected. Postgres as a container app with no volume keeps slice 1's "a
transaction survives a restart" **true locally and false deployed**, with
nothing reporting the difference.

### What the container says on the way up

Both of #23's predictions, confirmed on the first start:

```
warn: Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3]
      Failed to determine the https port for redirect.
Cannot load library libgssapi_krb5.so.2
```

The first is `UseHttpsRedirection` degrading to a no-op behind an ingress that
already terminated TLS -- harmless by degradation rather than by design, which
is #36's to settle. The second is Npgsql's loader message, written to stdout
with no log level and therefore unfilterable. Neither is a fault. Also visible:
`Hosting environment: Production` with nothing set, and `Now listening on:
http://[::]:8080`.

```
az containerapp logs show -g rg-landmoney -n landmoney --type console --tail 40
```

## The cold start, measured

Not yet recorded -- the app had not scaled to zero by the time this was
written, and a number measured against a running replica would be a warm
start wearing a cold start's label. The measurement is a request to
`/api/transactions` after `az containerapp replica list` reports zero:

```
az containerapp replica list -g rg-landmoney -n landmoney --revision <revision> --query "length(@)" -o tsv
```

For reference, the numbers that *are* measured: the very first request to a
freshly created revision took **9.7 s**, and a warm request **0.2 s**.
Scale-in took longer than the nominal five-minute cooldown -- still one
replica six minutes after the last request.

## What this costs, and the date to remember

Three different things get called free and only one of them is a year.

**The Free Trial: 30 days, $200 credit, spending limit on.** While that limit is
on, exceeding the allowances *disables resources* rather than charging the card
-- which is the real protection, and worth confirming rather than assuming.

**At 30 days the subscription is disabled unless it is upgraded to
Pay-As-You-Go.** Upgrading is also what **removes the spending limit**, so that
is the moment the card becomes live. The subscription was created **2026-08-25**,
which puts that decision around **2026-09-24**.

**Twelve months of free services, to 2026-08-25 + 1 year**, and it covers
specific quantities of specific services rather than everything running: B1ms for
750 hours a month (more hours than a month has, so continuous), 32 GB storage,
32 GB backup. That is exactly the shape #34 specified, and the reason
`--storage-size 32` and `--storage-auto-grow Disabled` are not arbitrary.
Afterwards, roughly **15-20 USD a month**.

**Container Apps is a different allowance again** -- a permanent monthly grant
rather than a twelve-month one, and with `--min-replicas 0` this app sits far
inside it.

**The Log Analytics workspace is the one to watch.** It is created by
`env create` without being asked for, its ingestion is covered by neither of the
above, and it is therefore the likeliest source of a surprise line on a bill.

Set a budget with an alert. **Not from the CLI**: `az consumption budget create`
in this version has no notification parameters at all, so it would create a
budget that never tells anyone anything -- worse than none, because it looks
like protection. Portal, **Cost Management + Billing -> Budgets -> Add**, with
thresholds and an email address.

These numbers are off Azure's published terms on 2026-08-25 and Azure changes
them. Cost Management -> Free services shows the live consumption against each
allowance, and is the authority over this section.

## Tearing it all down

One command, and it is the reason everything went into one resource group:

```
az group delete --name rg-landmoney --yes --no-wait
```

This deletes the database and its backups. There is nothing else to clean up --
the Log Analytics workspace is inside the group too.
