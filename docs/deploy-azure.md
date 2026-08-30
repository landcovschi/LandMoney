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

**The automated version of this is #38, and it is step 14.** These commands are
what it transcribes, which is the reason for doing it by hand first: a deployment
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
| Categorizer app | `landmoney-categorizer` | Internal ingress -- no URL at all, see step 16       |
| Storage account | `stlandmoneypl`     | The Data Protection key ring -- one blob, see step 15   |
| Key vault       | `kv-landmoney-pl`   | Holds the key that wraps it, see step 15                |

**Why the database's full host name is `<server>` below and not spelled out**,
decided in #36. The container app's FQDN is written out everywhere in this
repository and should be -- it is a public website, and its whole job is to be
reachable. The database's FQDN is the opposite: password authentication, opened
to every Azure tenant by the 0.0.0.0 rule in step 5, and this repository is
public. Publishing `host + username` hands out two of the three things needed
to try the third.

Read as a security control that is theatre, and it is not one: the table above
still names the server, the suffix is the same for every Flexible Server on
earth, and anyone who wants the string can assemble it in five seconds. What it
is worth is that the string does not exist here ready to be pasted into a
scanner, and that grepping the repository for the deployed host name answering
nothing stays a check that means something the day a real connection string is
nearly committed. The controls remain the password and enforced TLS, exactly as
before.

That check has to be run with the host name typed on the command line rather
than written into a file, or the file that documents it becomes the thing it
finds -- which is how this paragraph was first written, and what the check
itself reported.

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

**This step is the throwaway that #37 exists to replace, and #37 has now
replaced it -- step 13 is what to run.** It is kept here rather than deleted
because the run below is what created the schema this database still carries,
and because the two `dotnet ef` outputs it teaches how to read apply to the
bundle unchanged.

`dotnet ef database update` from a developer machine is the first of the three
mechanisms the roadmap lists and the one it expects to lose: it needs the SDK, the tools, and a
firewall rule for whoever runs it. It is here only because #35's acceptance test
needs `/api/transactions` to answer with something.

The password comes from the variable, so the line typed contains only its name
and the shell history records only that:

```
$env:ConnectionStrings__Default = "Host=<server>.postgres.database.azure.com;Port=5432;Database=landmoney;Username=landmoney;Password=$pgPlain;SSL Mode=Require;Timeout=15;Command Timeout=30"
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
$pgConn = "Host=<server>.postgres.database.azure.com;Port=5432;Database=landmoney;Username=landmoney;Password=$pgPlain;SslMode=Require;Timeout=15;CommandTimeout=30"
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

**23.3 seconds**, measured 2026-08-25 on a request to `/api/transactions` taken
one minute after `replica list` first reported zero. The warm request that
followed it took **0.23 s** -- a factor of a hundred.

```
20:11:05  replicas='1'
20:11:37  replicas='0'
COLD START: 23.3s  status 200
WARM:       0.23s  status 200
```

**Scale-in took about fourteen minutes, not the configured five.** The scale
block reads `cooldownPeriod: 300, pollingInterval: 30, rules: null`, and the
replica stayed `Running` for roughly fourteen minutes after the last request
before disappearing. So the cooldown is a floor rather than a schedule, and
anything that measures a cold start has to wait for `replica list` to actually
report zero rather than trusting the clock:

```
az containerapp replica list -g rg-landmoney -n landmoney --revision <revision> --query "length(@)" -o tsv
```

### What 23 seconds costs, and where

**Opening the URL cold is fine.** The *document* request pays the 23 seconds,
and a browser has no timeout of its own on a page load, so the page simply takes
a while. By the time the client's JavaScript runs its first `fetch`, the
container is warm and the API answers in fractions of a second.

**A tab left open is the case that breaks.** `src/landmoney.client/src/api/transactions.ts`
sets `REQUEST_TIMEOUT_MS = 10_000`, with a comment saying ten seconds is
"generous for a Postgres on the same machine". That was true when it was
written and is not true here: after ~14 idle minutes the app scales to zero, and
the next `fetch` from an already-loaded page meets a cold container, gives up at
10 s, and shows the timeout message. A retry then succeeds, because the first
attempt started the container.

This is **not fixed here** -- it is adjacent to #35 and belongs to whoever picks
it up. Worth knowing before choosing: raising the constant makes a real hang
take longer to report, which is the exact failure the timeout exists to prevent.
The alternatives are a longer timeout only on the first request of a session, a
warm-up request fired when the page loads, or `--min-replicas 1`, which gives up
the reason Container Apps was chosen over App Service in the first place.

It also lands directly on the roadmap's own bar for this slice, **"the URL works
from a phone"** -- on a phone the first interaction after a pause is exactly this
case.

## Step 12 -- configuration, and where it lives

**This is #36**, and half of it was already done in step 10 because doing it the
other way round would have left the password readable. What follows is the whole
picture in one place, since configuration is the part of a deployment that is
invisible in a diff.

There are exactly three places a setting can live, and each is chosen for a
reason:

| Where                            | What is in it                              | Why there                                                                        |
| -------------------------------- | ------------------------------------------ | -------------------------------------------------------------------------------- |
| `appsettings.json`, in git       | Log levels, and nothing else               | It is public. Anything here is published                                          |
| User-secrets, on this machine    | The **local** connection string            | A development-machine feature. It does not exist in a container                    |
| A Container Apps **secret**      | The **deployed** connection string         | The only one of the three that is neither in git nor tied to one developer's disk |

The application cannot tell the difference. `builder.Configuration` reads
environment variables in every environment, and `ConnectionStrings__Default`
becomes the key `ConnectionStrings:Default` -- which is what
`GetConnectionString("Default")` asks for, and what user-secrets fills locally.
One line in `Program.cs`, three sources, no branch on environment anywhere.

**`ConnectionStrings__Default`, with two underscores.** The environment variable
provider maps `__` to `:` because a colon is not legal in a variable name on
every platform. A single underscore is not an error -- it produces the key
`ConnectionStrings_Default`, which nothing reads, so the application fails at
startup with the user-secrets message from `Program.cs` and sends whoever reads
it to the wrong machine entirely.

### The environment name, set rather than defaulted

```
az containerapp update -g rg-landmoney -n landmoney --set-env-vars ASPNETCORE_ENVIRONMENT=Production
```

**This changes no behaviour at all, and is worth running anyway.** Measured in
#35 before it was set: the container already logged `Hosting environment:
Production`, because the ASP.NET Core default when the variable is absent *is*
Production, and the `aspnet` base image does not set it. So this is not a fix.
What it buys is that the value is now a declared fact instead of a default --
and what hangs off it is not cosmetic. `Program.cs` gates `UseExceptionHandler`,
`UseHsts`, `UseHttpsRedirection` and now `UseForwardedHeaders` on
`!app.Environment.IsDevelopment()`. A one-word typo in that variable, set by
anything later, silently turns all four off. Written down, it is a line in
`az containerapp show`; defaulted, it is nowhere.

**`--set-env-vars` adds and updates; `--replace-env-vars` removes everything
else.** They are one word apart in the same help text, and the wrong one here
deletes `ConnectionStrings__Default` and leaves an app that starts, throws at
`Program.cs`, and reports a missing user secret. The CLI's own wording, which is
the thing to read rather than a blog post:

```
--set-env-vars      : Add or update environment variable(s) in container.
                      Existing environment variables are not modified.
--replace-env-vars  : Replace environment variable(s) in container. Other
                      existing environment variables are removed.
```

So the check after running it is not "did it succeed" but "is the other one
still there":

```
az containerapp show -g rg-landmoney -n landmoney --query "properties.template.containers[0].env" -o json
```

```
[
  { "name": "ConnectionStrings__Default", "secretRef": "pgconn", "value": "" },
  { "name": "ASPNETCORE_ENVIRONMENT", "value": "Production" }
]
```

That output is also #36's acceptance test, and the **empty** `value` beside the
`secretRef` is the whole of it: the field exists and holds nothing, because what
fills it is resolved when the container starts and never travels back out. A
secret referenced this way is not returned by `show`, by the portal, or in a
revision's template -- `az containerapp secret list` without `--show-values`
answers with names only. (Before this command was run, `show` omitted the
`value` key entirely rather than printing it empty. Same meaning, different
shape, and worth recognising rather than reading as a change.)

**What the update actually did, read back rather than assumed:**

```
az containerapp revision list -g rg-landmoney -n landmoney --all -o table

Name                Active    Created
landmoney--r7hjn68  False     2026-08-25T16:53:33+00:00
landmoney--0000001  True      2026-08-25T20:33:14+00:00
```

Both worth noticing. **`revision list` without `--all` shows only active
revisions**, so the one that was just replaced looks deleted rather than
deactivated -- and it is not deleted; it is retained, and it is what a rollback
targets. And the new revision is named `0000001` where `create` produced the
random `r7hjn68`: an update with no `--revision-suffix` numbers them
sequentially, so revision names in this app do not share one shape and cannot be
sorted to find the newest. `createdTime` can.

### Changing a secret needs a new revision

Container Apps revisions are immutable. `az containerapp secret set` updates the
app's secret store, and **the running revision keeps serving the old value**, so
a configuration change that appears to have done nothing is almost always this:

```
az containerapp secret set -g rg-landmoney -n landmoney --secrets "pgconn=$pgConn"
az containerapp revision restart -g rg-landmoney -n landmoney --revision <name>
```

`az containerapp update` of any kind creates a new revision on its own, which is
why the environment-variable command above needs no restart. Setting only a
secret does not.

### The one thing here that needs a new image

`UseForwardedHeaders` is a code change, so it reaches Azure only when an image
containing it does. Nothing above deploys it. Until then the deployed app is the
previous image and its `Strict-Transport-Security` header stays absent -- which
is the verification, so it is also the tell:

```
az containerapp update -g rg-landmoney -n landmoney --image ghcr.io/landcovschi/landmoney:sha-<40 characters>
curl -sSI https://<app>.polandcentral.azurecontainerapps.io/ | grep -i strict-transport
```

The SHA is the merge commit on `main`, and `ci.yml` writes the digest to the run
summary. **This is the step #38 exists to delete** -- it is gone as of step 14 --
and doing it by hand once is the point.

**Done, and what it answered.** Revision `landmoney--0000002` on
`sha-25720b96132412609096b4844c6ef33c255f2a6f`:

```
HTTP/1.1 200 OK
server: Kestrel
cache-control: no-cache
strict-transport-security: max-age=2592000
```

Two claims that were reasoning until this request, and are now measurements.
**The ingress does send `X-Forwarded-Proto`** -- nothing in this application
echoes a request header, so there was no way to see it from the inside, and the
HSTS header appearing is the only proof available that the scheme arrived.
And the startup log no longer carries `Failed to determine the https port for
redirect`, which has been printed at every start since #23 predicted it: the
redirect is now a no-op because there is nothing to redirect, rather than
because it cannot find a port.

Two traps in checking this, both met. The tag has to be the **merge commit**,
and a `$(git rev-parse HEAD)` evaluated on the feature branch names a commit no
image was ever built for -- `ci.yml` publishes on pushes to `main` only, so the
tag simply does not exist. And these `curl | grep` lines are shell, not
PowerShell: `grep` is not a cmdlet, and the PowerShell spelling is

```powershell
curl.exe -sSI https://<app>.polandcentral.azurecontainerapps.io/ | Select-String strict-transport
```

with `curl.exe` written out, because in Windows PowerShell 5.1 a bare `curl` is
an alias for `Invoke-WebRequest`, which takes none of these arguments.

## Step 13 -- the schema, as a deployment step

**This is #37, and it replaces step 8.** Step 8 ran `dotnet ef database update`
from this machine to give #35 something to test against; it needs the SDK, the
tools and the source, which is three things a deployment should not need.

What runs instead is `efbundle` -- a single executable built by `ci.yml` from
the commit being deployed, holding the migrations, EF Core, the Npgsql provider
and the .NET runtime. `dotnet ef migrations bundle` produces it; the two flags
that matter are in the workflow with the reasoning beside them.

### Getting it

The `build` job uploads it as an artifact called `efbundle`. Take it from the
run that built the commit being deployed, into a folder outside the repository
-- 128 MB of runtime has no business in a working tree, and `.gitignore` and
`.dockerignore` name it only as a second line of defence:

```powershell
$run = "$env:TEMP\efbundle"; New-Item -ItemType Directory -Force $run | Out-Null; gh run download <run-id> --repo landcovschi/LandMoney -n efbundle -D $run
```

**It will not run on this machine, and that is deliberate.** It is a linux-x64
ELF binary; Windows answers with a format error rather than anything helpful.
Run it in the smallest image that can host it -- the same base the application's
own runtime image is built on, minus ASP.NET:

```powershell
docker run --rm -v "${run}:/w" -w /w -e "ConnectionStrings__Default=$pgConn" mcr.microsoft.com/dotnet/runtime-deps:10.0 sh -c "chmod +x ./efbundle && ./efbundle"
```

Two things in that line are load-bearing:

- **`chmod +x`.** A GitHub artifact is a zip, and a zip does not carry the
  executable bit. Without it the answer is `Permission denied` on a file that is
  plainly sitting there.
- **`runtime-deps`, not `runtime` or `aspnet`.** `--self-contained` bundles the
  .NET runtime but not glibc, ICU and OpenSSL, which is what that image is.

**Two things that are NOT load-bearing, written down because they were guessed
here first and then measured.** The first draft of this section claimed the
`-e` pair had to be quoted as a whole -- `-e "NAME=$pgConn"` rather than
`-e NAME="$pgConn"` -- on the reasoning that a connection string is
semicolon-separated and `;` ends a statement in PowerShell. It does not: the
semicolons arrive inside an expanded string, PowerShell does not re-parse them,
and both spellings deliver the value intact, a value containing a space
included. The same draft said the download folder must have no space in its
path; a bind mount from `$env:TEMP\bundle space test` works, quoted the way it
is above. Both were plausible, neither was true, and the cost of checking was
two `docker run`s against `printenv`.

**These are PowerShell**, which is the part that *does* matter -- the same
lesson #53 recorded for `curl.exe`. From Git Bash the `docker` line needs
`MSYS_NO_PATHCONV=1` in front of it, or the shell rewrites `/w` into a Windows
path and docker answers `the working directory 'W:/' is invalid`. That one was
met rather than guessed.

**`Cannot load library libgssapi_krb5.so.2` appears here too**, for the same
reason it appears in the application's logs and with the same non-consequence:
Npgsql probes for GSSAPI, password authentication does not use it, and the
queries run. It is written by the loader rather than through `ILogger`, so it
carries no level and cannot be filtered.

### The connection string, from the one place that already holds it

#37 names the trap: the bundle needs the connection string, and that is the
secret from #36 arriving in a second place. Two places holding one secret is how
they drift. So it is not typed and not stored anywhere new -- it is read back
out of the Container Apps secret that the running app already uses:

```
$pgConn = az containerapp secret show -g rg-landmoney -n landmoney --secret-name pgconn --query value -o tsv
```

That is the only thing in this file that reads a secret back, and it is why
`secret show` exists at all. It also means rotating the password stays a single
act: `az containerapp secret set`, and the next deployment reads the new value.

**By environment variable, never by `--connection`.** The bundle accepts
`--connection <CONNECTION>` and it is what its own `--help` lists first, but an
argument is visible in the process list and in any log that echoes the command.
Measured rather than assumed -- the bundle runs the application's own
configuration pipeline, so `ConnectionStrings__Default` reaches it exactly the
way it reaches the app:

```
LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE
Applying migration '20260825204735_TransactionListIndex'.
Done.
```

### Done, and what it answered

Run against `psql-landmoney-pl` on 2026-08-26, from the artifact of the pull
request's own CI run rather than from a bundle built by hand:

```
Applying migration '20260825204735_TransactionListIndex'.
CREATE INDEX ix_transactions_occurred_at_created_at ON transactions (occurred_at, created_at);
INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
VALUES ('20260825204735_TransactionListIndex', '10.0.10');
Done.
```

Three things that were reasoning until this ran, and are now measurements. The
linux-x64 artifact **does** run out of a GitHub zip in `runtime-deps` -- so
`--self-contained` covers what it claims to and `chmod +x` covers the rest.
`ConnectionStrings__Default` read out of the Container Apps secret **does**
reach it, so there is still exactly one place holding that password. And the
migration reached the deployed database with **no image deployed and no
revision created**, which is the whole shape of #37: the schema and the
application move on separate tracks.

Note the `10.0.10` in that INSERT -- the pinned `dotnet-ef`, recorded in the
history table by the bundle that applied it. It is the version of the tool, not
of the database.

### Running it twice

The second run of the same bundle, unchanged, against the same database:

```
No migrations were applied. The database is already up to date.
Done.
```

That is #37's acceptance test, and it is a property of `__EFMigrationsHistory`
rather than of the bundle: EF reads which migrations are recorded and applies
the difference.

### What happens when it fails halfway

Decided in advance, because after the fact there is no time to have the
conversation -- and measured on a throwaway database with two extra migrations,
the second of which contained deliberate nonsense.

**A migration is atomic; a run of migrations is not.** The broken migration
created a table before its bad statement, and that table does not exist
afterwards -- Postgres has transactional DDL and Npgsql wraps each migration in
its own transaction. The migration before it is applied and recorded:

```
migration_id
-------------------------------------
 20260818192031_InitialCreate
 20260825204735_TransactionListIndex
 20260825210000_ScratchGood
(3 rows)
```

So the schema is left between two states, and `__EFMigrationsHistory` says
accurately which one.

**The answer is therefore fix forward, not restore from backup.** Because the
history is accurate, re-running a corrected bundle resumes at exactly the
migration that failed:

```
Applying migration '20260825210001_ScratchBroken'.
Done.
```

Restoring from backup stays available -- Flexible Server takes them and #34
counted them in the bill -- and it is the answer for a migration that
*succeeded* and destroyed data, which is a different accident. For a migration
that threw, the backup is the slower route to the same place.

The one shape this reasoning does not cover is a migration Postgres cannot run
inside a transaction, `CREATE INDEX CONCURRENTLY` being the one that will come
up first. There is none here, and the day there is, it fails halfway with no
rollback and this section needs rewriting.

### The order against the app deployment

**Migrate first, then deploy the revision.** For the interval between the two,
the old revision runs against the new schema.

That is safe today because every migration so far only adds: `InitialCreate`
built the table, `TransactionListIndex` adds an index, and code that has never
heard of an index is unaffected by one existing. It stops being safe for a
rename or a drop, where the old revision would query a column that is gone --
and no ordering fixes that. Expand-and-contract does, in three deployments
instead of one, and nothing here needs it yet.

The other order -- deploy first, then migrate -- would put the new revision
against the old schema, which for an added column is a query naming a column
that does not exist. Strictly worse for the changes this project makes.

```
az containerapp update -g rg-landmoney -n landmoney --image ghcr.io/landcovschi/landmoney:sha-<40 characters>
```

**Both of these were hand steps until #38, and step 14 is where they are
joined.** There the bundle is downloaded from the same run that built the image,
so the two cannot disagree about which commit is being deployed -- which is the
argument for building it in `build` rather than in a job of its own.

**One thing #38 has to measure rather than assume, and still does:** whether a
GitHub-hosted runner can reach the database at all. Step 14 carries the fallback
if it cannot. The firewall is the 0.0.0.0 "all Azure
services" rule from step 5 plus this machine's address, and whether a runner's
outbound address falls inside the first is not something this file can answer.
If it does not, the shape is a temporary firewall rule created and removed by
the deploy job, which the OIDC login #38 already needs makes possible.

## Step 14 -- the same thing, from a workflow

**This is #38, and it is what deletes two hand steps: the
`az containerapp update --image` at the end of step 12, and the `docker run` of
step 13.** Everything the `deploy` job in `.github/workflows/ci.yml` runs is a
command from this file, in the order this file established. That order was the
reason for doing it by hand first -- a deployment written straight into a
workflow fails inside a runner where nothing can be inspected.

What stays a hand step, exactly once, is the identity the workflow logs in as.
Claude does not authenticate; the commands below are the owner's.

### The identity, and why it is not a password

The tutorial answer is `az ad sp create-for-rbac --sdk-auth`, whose output goes
into `secrets.AZURE_CREDENTIALS`. It works. It is also a password with a long
life, kept in a store designed to hand it to any workflow in the repository, and
it expires without warning at the worst possible moment.

What replaces it is an app registration with **no credential at all**, plus a
statement in Entra ID of the form "a token issued by GitHub Actions, for this
repository, on this branch, may act as this app". GitHub mints such a token per
run; `azure/login` trades it for an Azure one that dies with the job. There is
nothing to rotate and nothing to leak.

### The commands, run once

PowerShell, from anywhere. `az login` first, as the account that owns the
subscription.

```powershell
$appId = az ad app create --display-name "github-landmoney-deploy" --query appId -o tsv
$spId  = az ad sp create --id $appId --query id -o tsv
```

**`az ad app create` may be refused in a work tenant**, where creating
registrations is a directory privilege rather than something every user has. It
is granted by default in the "Default Directory" tenant that signing up with a
personal address produces, which is what this subscription has.

The federated credential is a JSON document, and it goes through a file rather
than an inline string -- PowerShell, `az`'s own parser and the `.cmd` shim
between them each have an opinion about quotes, and a file has none. **Do not
type the subject from the template below** -- read it out of GitHub first, for
the reason directly underneath:

```powershell
gh api repos/landcovschi/LandMoney/actions/oidc/customization/sub --jq .sub_claim_prefix
```

```powershell
@'
{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "<sub_claim_prefix>:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}
'@ | Set-Content -Encoding utf8 fedcred.json

az ad app federated-credential create --id $appId --parameters fedcred.json
```

**The `subject` must match what the run presents, character for character**, and
this is #38's first trap. It bit on the first deployment, in the one way neither
the issue nor the documentation predicts: **the subject GitHub sends is not the
`repo:owner/name:ref:...` that every guide shows.** It carries immutable numeric
ids, and the run says so precisely:

```
AADSTS700213: No matching federated identity record found for presented
assertion subject 'repo:landcovschi@257582719/LandMoney@1324374880:ref:refs/heads/main'.
Check your federated identity credential Subject, Audience and Issuer against
the presented assertion.
```

Which is a good error -- it prints the string it wanted, so the fix is a copy.
The confirmation that this is a default rather than something someone switched
on is the API above, and it is worth reading twice:

```
{"use_default":true,"use_immutable_subject":false,
 "sub_claim_prefix":"repo:landcovschi@257582719/LandMoney@1324374880"}
```

`use_default` is true, `use_immutable_subject` is **false**, and the effective
prefix is the immutable one anyway. So there is no flag here to have set wrongly
and nothing to switch back; the numeric form is simply what the default now
produces. It is also the better string on its merits, which is presumably why:
`257582719` and `1324374880` survive a rename of the account or the repository,
where the names do not.

What that costs: the subject stops reading as anything and has to be fetched
from the API rather than typed. Hence the `gh api` line above the template.

Three more ways to get it wrong, all of which fail as that same error:

- `<prefix>:environment:production` is a **different subject** from the `ref:`
  one. It is what to use if the `deploy` job is ever given
  `environment: production` -- and then it is the only one that works, because a
  job with an environment presents that subject and not the branch one. Which is
  the reason the job has no environment: one fewer string that must agree with a
  string in another system.
- The repository name is **case-sensitive** here, numeric ids or not.
  `LandMoney` is what GitHub puts in the token; `landmoney` is the image name,
  and they are not interchangeable -- the same trap as #24's, one system further
  along.
- `refs/heads/main` and not `main`.

Read it back rather than trusting the paste:

```powershell
az ad app federated-credential list --id $appId --query "[].{name:name,subject:subject}" -o table
```

Changing it later is an `update` on the existing credential rather than a delete
and a create, which is what the first deployment needed:

```powershell
az ad app federated-credential update --id $appId --federated-credential-id github-main --parameters fedcred.json
```

Then the permission. `Contributor` on the resource group, which is what the
workflow needs and no more than the resource group holds:

```powershell
$subId = az account show --query id -o tsv
az role assignment create --assignee-object-id $spId --assignee-principal-type ServicePrincipal --role Contributor --scope "/subscriptions/$subId/resourceGroups/rg-landmoney"
```

**`--assignee-object-id` with `--assignee-principal-type`, not `--assignee`.**
The short form does a Graph lookup to work out what kind of principal the id
names, and an account without directory read permission gets a failure about the
assignee that reads like a bad id. Passing both facts skips the lookup.

**Run the `--scope` commands from PowerShell, not Git Bash**, and this is the
third outing of one lesson rather than a new one. Git Bash rewrites an argument
that looks like a Unix path into a Windows path before the program sees it, so
`--scope "/subscriptions/<sub>/resourceGroups/rg-landmoney"` arrives at ARM as
something under the Git installation directory, and ARM answers with the only
thing it can:

```
ERROR: (MissingSubscription) The request did not have a subscription or a valid
tenant level resource provider.
```

An error about a subscription, for a cause that is the shell. It cost two runs
here before the tell was noticed: `az role assignment list --resource-group
rg-landmoney` works while `az role assignment list --scope
"/subscriptions/.../resourceGroups/rg-landmoney"` does not. Same call, same
permissions, same account -- the only difference is one argument shaped like a
path. `MSYS_NO_PATHCONV=1` in front of the command fixes it and confirms the
diagnosis; PowerShell never had the problem. Step 13 records the same rewrite
turning `docker run -w /w` into `the working directory 'W:/' is invalid`, and
#53 recorded `curl` being an alias for `Invoke-WebRequest` in the other
direction. Every runbook line here is PowerShell for that reason.

Narrower scopes were considered and lost. The container app alone is not enough:
reading the connection string back needs
`Microsoft.App/containerApps/listSecrets/action`, which no built-in reader role
carries, and the day the database firewall needs a temporary rule the scope has
to reach the Postgres server anyway. The resource group is the unit everything
here is created in and deleted in, so it is the honest boundary.

Finally the three ids, as repository **variables** and not secrets -- they
identify an app registration, they are not a credential, and filing them as
secrets would suggest there is something here to leak:

```powershell
gh variable set AZURE_CLIENT_ID --body $appId
gh variable set AZURE_TENANT_ID --body (az account show --query tenantId -o tsv)
gh variable set AZURE_SUBSCRIPTION_ID --body $subId
```

```powershell
Remove-Item fedcred.json
```

### What the job then does

Four steps, all of them from this file:

1. `actions/download-artifact` takes `efbundle` from **this same run**, so there
   is no checkout in the job at all. The commit that built the bundle and the
   commit whose image is deployed are then the same by construction rather than
   by a `git checkout` that could differ from both.
2. `az containerapp secret show` reads the connection string out of the one
   place that already holds it -- step 13's answer to "two places holding one
   secret", now automated -- and `::add-mask::` keeps it out of the log.
3. `./efbundle` applies migrations. No `docker run` wrapper: that exists in step
   13 because the bundle is a linux-x64 binary on a Windows machine, and a
   `ubuntu-latest` runner is the platform it was built for. `chmod +x` still
   applies -- a zip carries no executable bit wherever it is unpacked.
4. `az containerapp update --image ...:sha-<github.sha>` makes the revision, and
   then the job asks Azure what image the app is running and requests
   `/api/transactions` over the public URL. A deployment that reports success
   while the site is down is the failure slice 3 exists to remove.

### Two things the workflow settles that this file could not

**Concurrency.** The workflow-level `concurrency` block cancelled in-progress
runs, and a run cancelled between "migrations applied" and "revision replaced"
is worse than one that waits. #38 suggests a separate group on the deploy job;
that does not work, because cancellation happens to the whole run and takes
every job with it. What does work is `cancel-in-progress:
${{ github.event_name == 'pull_request' }}` -- pull requests keep the old
behaviour, and pushes to `main` queue instead, since a concurrency group without
cancellation makes the second run wait for the first.

**The firewall, answered.** Step 13 left it open: the rule is the 0.0.0.0 "all
Azure services" one from step 5, and whether a GitHub-hosted runner's outbound
address falls inside it could not be answered from here. **It does.** The
`Apply migrations` step of the first deployment connected and reported

```
No migrations were applied. The database is already up to date.
Done.
```

in seven seconds, which is the whole answer: a runner reaches the server, and
the bundle applied on 2026-08-26 by hand had already recorded its migration.
GitHub's hosted runners are Azure virtual machines and that rule admits Azure,
so the reasoning was right -- it is now measured rather than likely.

**Which is also the argument against that rule getting any narrower.** It
already admits every Azure tenant's resources; it now demonstrably admits a
GitHub runner too. Nothing here changes the conclusion of #34 -- the password
and enforced TLS are the controls -- but it is one more reason the question to
reopen alongside Neon at the end of the free year is "is this still the right
shape" rather than "is this still cheap".

Kept for the day it stops being true, since a rule this broad is exactly the
kind of thing that gets tightened: the fallback is a firewall rule created and
removed by the job, which the OIDC login already makes possible.

```
ip=$(curl -sS https://api.ipify.org)
az postgres flexible-server firewall-rule create -g rg-landmoney -n psql-landmoney-pl --rule-name ci-$GITHUB_RUN_ID --start-ip-address "$ip" --end-ip-address "$ip"
```

with the matching `firewall-rule delete` in a step carrying `if: always()`, or
the rules accumulate one per deployment for ever.

### What the first deployment measured

Run 32962766830, revision `landmoney--0000003` on
`sha-3f467199c8f97df8e7808e25a8fd8d8a9949fd5d` -- the merge commit of #55.

| Step                            | Took |
| ------------------------------- | ---- |
| Download the migration bundle   | 2 s  |
| Log in to Azure                 | 5 s  |
| Install the containerapp extension | 10 s |
| Apply migrations                | 7 s  |
| Deploy the revision             | 19 s |
| Check what is running           | 26 s |
| **The whole job**               | **71 s** |

Three things worth keeping. **`az extension add` is 10 s of a 71 s job**, and it
is the one step that does nothing but prepare -- worth remembering before
anything else gets blamed for the length. **The verification's retry never
fired**: the first `curl` answered 200, so the loop printed no `attempt` line at
all. It stays, because "the revision was provisioned before it was serving" is a
race and this run winning it is not evidence there is no race. And the job spent
**19 s on `containerapp update`** against the 23.3 s cold start #35 measured,
which is the difference between provisioning a revision and starting one from
zero.

**The failure that came first landed exactly where the design put it.** The
login failed -- the subject above -- and the job stopped at step 2 of 6, before
`containerapp secret show`, before the bundle ran and before any revision
existed. `build` and `publish` were green, so the image for that commit was
already on `ghcr.io` and the fix needed no new commit, only a re-run. Azure was
never touched, and the previous revision served throughout.

### Rolling back

Nothing new, and worth writing beside the automation rather than remembering it
during an incident. `update` deactivates the previous revision instead of
deleting it:

```
az containerapp revision list -g rg-landmoney -n landmoney --all -o table
az containerapp update -g rg-landmoney -n landmoney --image ghcr.io/landcovschi/landmoney:sha-<the previous 40 characters>
```

The image tag is the whole reason this is possible, and the reason #24 refused
to deploy `latest`: a revision pinned to a moving tag cannot say what it is
running, and rolling one back means first working out what `latest` used to be.

A schema that moved with it is the case this does not cover. Re-deploying an
older image does not un-apply a migration, and nothing here generates a down
migration -- while every migration only adds, an older image against a newer
schema is exactly the safe direction, and the day one drops a column that stops
being true.

## Step 15 -- who may use it

Until #52 the deployed URL was open. Anyone holding it could read every
transaction and write new ones; there was no account, no owner and no check
anywhere.

What #52 landed is a username and a password, held by this application: ASP.NET
Core Identity, a login form in the client, and an invite code required to create
an account. There is no identity provider to register with and no redirect
anywhere -- so unlike almost every other step in this file, **most of this one is
configuration rather than commands**.

The reasoning, including what Easy Auth and OpenID Connect lost on, is in
`CLAUDE.md`.

### The one setting

Registration is refused unless the request quotes `Authentication:InviteCode`.
It is a shared secret, so it goes in as a Container Apps secret and is referenced
rather than pasted -- the same road the connection string takes (#36):

```
az containerapp secret set -g rg-landmoney -n landmoney --secrets invite-code=<a long random string>
```

```
az containerapp update -g rg-landmoney -n landmoney --set-env-vars "Authentication__InviteCode=secretref:invite-code"
```

Double underscores, not a colon: that is how a nested configuration key is
spelled as an environment variable. `--set-env-vars` adds and
`--replace-env-vars` removes everything else -- one word apart in the same help
text, and the wrong one deletes the connection string.

Generate the value rather than inventing one. Anything long and random will do;
this produces one without it reaching a shell history file:

```
python -c "import secrets; print(secrets.token_urlsafe(24))"
```

**Set this before the image that needs it is deployed.** The current revision has
never heard of the variable and ignores it, so there is no window where the site
is wrong -- and the alternative order leaves the deployed application unable to
create the first account. Same reasoning as migrate-first (#37): the thing that
is needed arrives before the thing that needs it.

**What happens if it is not set.** Registration is refused for everybody and one
error is logged at startup naming the key. Existing accounts still sign in, and
nothing else changes -- the missing secret closes the door to new accounts rather
than to the application. It is deliberately not a startup throw: `efbundle` runs
`Program.cs` from a directory with no configuration at all, and #57 is what that
costs.

### The first account

There is no seeding and no bootstrap command. Open the site, follow *I have an
invite code and need an account*, and fill in three fields. The account that
registers first has nothing special about it -- there are no roles, and every
account sees only its own rows.

Passwords are ten characters minimum, with no requirement about digits or
symbols. Five wrong attempts lock the account for five minutes.

### Claiming the rows that were there first

`owner_id` is nullable and the migration backfills nothing, deliberately: the
database does not know who entered the rows written before #52, because the fact
was never recorded, and inventing a value at migration time would be a claim
about ownership that is not true.

The consequence is concrete and looks like data loss if it is not expected:
**after the migration, and before this step, the site is empty.** Every existing
row has a null owner and the query filter makes it invisible to everybody. The
rows are all still in the table.

So: register, then read the id off `/api/me`, which answers about the caller and
nobody else. Then, once:

```
az postgres flexible-server execute -n psql-landmoney-pl -u landmoney -p <the password> -d landmoney -q "UPDATE transactions SET owner_id = '<the ownerId from /api/me>' WHERE owner_id IS NULL;"
```

Run it once and never again as written. A second run after somebody else has used
the site would hand their rows over too -- their `owner_id` is not null, so they
are safe from this exact statement, but a broader `WHERE` would not be. Safe
today, when there is one user by assumption, and the line to re-read the day that
stops being true.

### A forgotten password

There is no reset flow, because there is no email -- #52 left that out on
purpose, and `CLAUDE.md` has the argument. So this is an administrative act, and
it is the owner's.

The password is stored as a hash, so it cannot be read back or set with an
`UPDATE`. The two honest routes are to delete the account and register again with
the same invite code:

```
az postgres flexible-server execute -n psql-landmoney-pl -u landmoney -p <the password> -d landmoney -q "DELETE FROM asp_net_users WHERE normalized_user_name = 'ALICE';"
```

**`normalized_user_name`, upper-cased**, which is the column Identity actually
looks up by -- `user_name` holds what was typed and is not what a query should
match on.

The rows survive that, because `owner_id` is a plain string and not a foreign
key: they are simply invisible until the new account's id is written over them
with the same `UPDATE` as above. That is a property worth knowing rather than
discovering -- and it is the same property that made swapping the whole
authentication subsystem cheap.

The other route is a small one-off program using `UserManager.ResetPasswordAsync`
with a generated token. It is the correct answer and it is not written here,
because a program that can reset any password is a thing that then exists.

### The cookie that survives a restart

**#88**, and until it the paragraph here said this was the first thing to pick up
after #52. What it fixes is the one cost on that list the owner paid every single
day.

ASP.NET Core encrypts the authentication cookie with a Data Protection key ring.
With nothing configured, that ring is generated in memory and dies with the
process -- and with `--min-replicas 0` the process dies after roughly fourteen
idle minutes (#35). So **coming back to the site after a pause meant typing a
password again**, where under the OpenID Connect version that lived for one day
it would have been a redirect and no typing. Two sharper edges of the same cause:
a revision replaced mid-session signed everybody out, and the day `--min-replicas`
goes above 1 two replicas cannot read each other's cookies at all -- which stops
looking like "signed out after a pause" and starts looking like "signed out at
random".

The fix is two Azure resources, an identity to reach them with, and two
environment variables. The application half is `src/LandMoney.Web/Auth/DataProtectionSetup.cs`.

#### What it costs, read rather than guessed

Off the Azure retail prices API on 2026-08-30, for `polandcentral`:

| Meter                                  | Rate                  |
| -------------------------------------- | --------------------- |
| Blob, Hot LRS, data stored             | 0.0196 USD / GB month |
| Blob, Hot LRS, read operations         | 0.0043 USD / 10K      |
| Blob, Hot LRS, write operations        | 0.054 USD / 10K       |
| Key Vault Standard, operations         | 0.03 USD / 10K        |

Neither resource has a monthly base charge; both are billed per operation. The
key ring is one XML file of a few kilobytes, read once per process start and
written once every ninety days when a key rolls. At this application's usage that
is **a small fraction of one US cent a month** -- far below the resolution of the
bill, and about four orders of magnitude under the 15-20 USD Postgres faces when
the free year ends (#34).

That is the answer to #88's first trap, and it is worth stating in the shape the
trap asks for: small is not free, and this one is small enough that the arithmetic
was the cheap part. The thing actually being spent is a second and third resource
to keep track of, not money.

**RSA 2048, deliberately.** Key Vault charges "Advanced Key Operations" at
0.15 USD / 10K for RSA 3072 and above and for elliptic curve keys -- five times
the rate above. The number of operations here makes that difference meaningless in
money, and the reason for taking 2048 anyway is that it is what the wrap needs and
nothing here benefits from more.

#### The names, which follow the table at the top of this file

| What            | Name              | Note                                        |
| --------------- | ----------------- | ------------------------------------------- |
| Storage account | `stlandmoneypl`   | Globally unique; lower-case letters and digits only, 3-24 |
| Blob container  | `keyring`         | Holds one file                              |
| Key vault       | `kv-landmoney-pl` | Globally unique, and the name is held for 7 days after a delete |
| Key             | `dataprotection`  | RSA 2048, wrap and unwrap only              |

#### 1. The storage account and the container

```powershell
az storage account create -g rg-landmoney -n stlandmoneypl -l polandcentral --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false --allow-shared-key-access false
```

`Standard_LRS` and not one of the redundant tiers: losing this file costs one
sign-in, which is the same thing a redeployment already costs today.

**`--allow-shared-key-access false` is the flag that matters, and it has a
consequence one command later.** With it, the account key stops working entirely
and every request has to carry an Entra ID token -- which is what the application
does. Leaving it on would keep a bearer credential to the whole account alive
beside the identity that makes it unnecessary. What it costs is that the *owner*
also has no key, so creating the container needs a token, and holding Owner or
Contributor on the subscription **does not** grant data-plane access to blobs.
That is a separate role assignment, and it is the one people spend an afternoon
on:

```powershell
az role assignment create --assignee-object-id (az ad signed-in-user show --query id -o tsv) --assignee-principal-type User --role "Storage Blob Data Contributor" --scope (az storage account show -g rg-landmoney -n stlandmoneypl --query id -o tsv)
```

```powershell
az storage container create --account-name stlandmoneypl -n keyring --auth-mode login
```

`--auth-mode login` is not optional here for the same reason: without it the CLI
reaches for the account key that no longer exists, and the error talks about
credentials rather than about the flag two commands above.

**Nothing creates the blob itself.** The application writes `keys.xml` the first
time it makes a key, and an empty container is the correct starting state -- which
is also why the startup check in `DataProtectionSetup.VerifyKeyRing` accepts a
ring with no keys in it.

#### 2. The vault and the key

```powershell
az keyvault create -g rg-landmoney -n kv-landmoney-pl -l polandcentral --enable-rbac-authorization true --retention-days 7
```

`--enable-rbac-authorization true` uses Azure role assignments rather than the
vault's own access policies. It is the current default and is written out because
the two systems look alike and do not interact: a role assignment on a
policy-mode vault grants nothing, silently.

`--retention-days 7` is the floor. Soft delete cannot be turned off, so a deleted
vault holds its **globally unique name** for the retention period -- which is a
detail with no consequence until the day this is torn down and rebuilt, and then
it is the whole morning. Purge protection is deliberately left off for the same
reason; it is the right setting for a vault holding something irreplaceable, and
this one holds a wrapping key whose loss costs one sign-in.

Creating a key in an RBAC vault is a data-plane operation, so the same rule as the
storage account applies to the owner:

```powershell
az role assignment create --assignee-object-id (az ad signed-in-user show --query id -o tsv) --assignee-principal-type User --role "Key Vault Crypto Officer" --scope (az keyvault show -g rg-landmoney -n kv-landmoney-pl --query id -o tsv)
```

```powershell
az keyvault key create --vault-name kv-landmoney-pl -n dataprotection --kty RSA --size 2048 --ops wrapKey unwrapKey
```

**`--ops wrapKey unwrapKey` and nothing else.** This key exists to encrypt one
small XML document; a key that can also sign is a key that can be used for
something nobody intended.

#### 3. The identity that reads them

**This is #88's second trap and it is a real one.** The OIDC federation from step
14 belongs to the **workflow**: it is what `azure/login` trades a GitHub token for,
and it is how `ci.yml` runs `az containerapp update`. The **running container** is
a completely different principal, and it is the one that needs to read a blob and
unwrap a key. Confusing the two produces role assignments that are correct, on the
wrong identity, and a 403 that names neither.

```powershell
az containerapp identity assign -g rg-landmoney -n landmoney --system-assigned
```

```powershell
az containerapp show -g rg-landmoney -n landmoney --query identity.principalId -o tsv
```

That principal id is what the next two commands are about. System-assigned rather
than user-assigned: it is created and deleted with the app, so there is no orphan
to clean up and nothing to remember to attach when the app is recreated. The cost
is that recreating the app makes a **new** principal and both role assignments
have to be made again -- which is written into the teardown section at the bottom
of this file.

```powershell
az role assignment create --assignee-object-id <the principalId> --assignee-principal-type ServicePrincipal --role "Storage Blob Data Contributor" --scope "$(az storage account show -g rg-landmoney -n stlandmoneypl --query id -o tsv)/blobServices/default/containers/keyring"
```

```powershell
az role assignment create --assignee-object-id <the principalId> --assignee-principal-type ServicePrincipal --role "Key Vault Crypto User" --scope "$(az keyvault show -g rg-landmoney -n kv-landmoney-pl --query id -o tsv)/keys/dataprotection"
```

Three things in those two lines are decisions rather than syntax.

**`--assignee-object-id` with `--assignee-principal-type`, never `--assignee`.**
The friendly form looks up the principal in Microsoft Graph, and a managed
identity created seconds earlier has not replicated yet -- so the command fails
with `Cannot find user or service principal in graph database`, which reads like
the identity was not created. It usually was. The explicit form skips the lookup
and is also the one that works when the account running it cannot read Graph at
all.

**Both scopes reach past the resource to the thing inside it.** `Storage Blob
Data Contributor` on the account would grant every container it will ever have;
on `/blobServices/default/containers/keyring` it grants one. Same for the key.
Neither role has a read-only variant that would do -- the application creates a
key every ninety days and wraps it, so it genuinely needs to write.

**A scope is a string beginning with a slash, and on this machine that is the
trap `CLAUDE.md` records twice.** Git Bash rewrites an argument that looks like a
Unix path into a Windows one before `az` ever sees it, and the failure is an error
about something else entirely -- `MissingSubscription` was #38's. These commands
are run from PowerShell.

#### 4. Point the application at them

```powershell
az containerapp update -g rg-landmoney -n landmoney --set-env-vars "DataProtection__KeyRingBlobUri=https://stlandmoneypl.blob.core.windows.net/keyring/keys.xml" "DataProtection__KeyVaultKeyUri=https://kv-landmoney-pl.vault.azure.net/keys/dataprotection"
```

Double underscores, and `--set-env-vars` rather than `--replace-env-vars`, for
the reasons the invite code above already gives.

**Neither of these is a secret and neither is a Container Apps secret.** They name
two resources; reaching either needs the managed identity, which is not something
a URL carries. This is the same distinction `Categorizer:BaseUrl` draws in
`README.md`: a value being configuration is not the same as a value being a
secret.

**The key URI carries no version, and that is load-bearing.** Key Vault will
happily give out `.../keys/dataprotection/9a8b...`, and pinning it works until the
key is rotated, at which point the application keeps asking for a version that is
no longer current. Versionless, the wrap uses whatever is current and the unwrap
uses the version recorded inside the key ring XML, so rotation costs nothing and
old keys stay readable.

**Set these before the image that reads them is deployed**, the same order as the
invite code and for the same reason: the running revision has never heard of
either variable and ignores both, so there is no window in which anything is
wrong. `ci.yml` asserts all of this on every deployment -- the two variables and
the system-assigned identity -- in a step called *Check the key ring*, which is
last in the job precisely so that the first run after #88 merges fails there and
nowhere else if this section has not been run.

#### How it is checked

Three things, and they are #88's own acceptance tests.

**1. A session survives a new revision.** Sign in, then force one:

```powershell
az containerapp revision restart -g rg-landmoney -n landmoney --revision <the active revision>
```

Reload the page. Before #88 that is a login form; after it, the transactions.

**2. Two replicas accept each other's cookies.** Temporarily:

```powershell
az containerapp update -g rg-landmoney -n landmoney --min-replicas 2 --max-replicas 2
```

Sign in, reload several times, and watch that nothing signs out; the ingress
spreads requests across both. Then put it back to `--min-replicas 0
--max-replicas 1`, because a replica held around the clock is exactly the bill
#61 declined for the categorizer.

**3. Keys that cannot be read stop the application rather than being replaced.**
This is the one worth doing, because it is the failure the whole feature is shaped
around and the framework's own answer to it is wrong. Remove the vault role from
the app's identity, restart it, and read the log stream:

```powershell
az role assignment delete --assignee <the principalId> --role "Key Vault Crypto User" --scope "$(az keyvault show -g rg-landmoney -n kv-landmoney-pl --query id -o tsv)/keys/dataprotection"
```

The replica must fail to start, with `Data Protection key <id> is in the store and
cannot be decrypted` in its log. **What must not happen is a working site**: left
to itself the framework logs that at Warning, decides the key is ineligible, and
generates a fresh ring -- measured while writing #88, on a file-system store with
certificate protection standing in for these two:

```
3b. Unprotect threw CryptographicException: Unable to retrieve the decryption key.
3c. Protect SUCCEEDED -- so a new key ring was generated over the unreadable one
3d. keys on disk now: 2
```

Everybody signed out, nothing red, which is #88's bug arriving through the fix
for it. Put the role back afterwards and restart again.

#### On this machine, nothing

Neither variable is set locally and neither should be. Keys in memory are the
right answer on a machine restarted by its owner, on purpose, while they are
looking at it -- and `dotnet run` needs no Azure account, no `az login` and no
network for authentication to work. The application says so once at startup, at
Information, and that line is how the state is told apart from a deployment that
lost its configuration, where the same sentence is an Error.

**Half configured is refused outright**, in both places. Setting the blob without
the vault would start, persist, keep everybody signed in, and leave the key that
decrypts every session cookie sitting in a container as readable XML -- a
downgrade with no symptom at all. So the application throws at startup rather than
taking it, and `ci.yml` reads both variables rather than one.


## Step 16 -- the categorizer, as its own app

**#61.** Until this, `src/categorizer/` existed in `docker-compose.yml` and
nowhere else: nothing in Azure built it, pushed it or ran it. What that looked
like from outside is nothing at all, which is why it went unnoticed -- the
deployed app resolved `Categorizer:BaseUrl` to the `appsettings.json` default
`http://127.0.0.1:8000`, found nothing listening, and stored every transaction
with no category. The feature was not broken; it was absent, and the fallback of
#39 is what hid it.

### The decision, and what lost

**A separate Container App with internal ingress.** The categorizer is its own
app in the same environment, `landmoney-categorizer`, reachable only from inside
`cae-landmoney` and not from the internet. It has its own revisions, its own
scaling and its own log stream.

**What lost: a second container in the same app.** They would share a revision
and a lifecycle, `localhost:8000` would keep working with no configuration
change at all, and there would be one thing to deploy instead of two. It is
cheaper in moving parts, and it solves the cold start below for free -- uvicorn
would start in parallel with the .NET process, so by the time the app answered
its first request the categorizer would already be up.

It lost on what this project is for. `CLAUDE.md` says skill gained beats working
code where the two conflict, and a sidecar is two processes in a box: the thing
worth learning here is service-to-service inside a Container Apps environment --
internal ingress, an internal FQDN, a dependency with its own release. The
sidecar also couples two releases into one, so a change to the Python service
would replace the .NET revision and sign everybody out (step 15's Data
Protection note), which is a real cost rather than a theoretical one.

**`--min-replicas 0`, chosen rather than discovered.** This is #61's first trap.
The app scales to zero and takes 23.3 s to come back; a categorizer that also
scales to zero puts a second cold start on the path of a save, and the client
gives up after 8 s -- 2 s of which is the connect budget (#59). So **the first
save of a session may be stored with no category**, and every save after it
categorised. The alternative is `--min-replicas 1`, which keeps one replica warm
and therefore billed around the clock; it was weighed and declined for a service
one person uses weekly, against a subscription that already has a 15-20 USD a
month Postgres bill arriving when the free year ends (#34).

What makes that affordable is that the failure is exactly the one #39 designed
for: the transaction is saved, `category` is null, and the log says so. It is
not a lost save.

**Not measured yet:** the categorizer's own cold start. The image is 46 MB
against the .NET app's 350 MB and uvicorn starts in about a second, so it may
well fit inside the 8 s budget and make the paragraph above pessimistic. Measure
it the way the app's was measured -- wait for `replica list` to report zero,
then time one save -- and write the number here.

### Create it

The image has to exist first: `publish` in `ci.yml` pushes
`ghcr.io/landcovschi/landmoney-categorizer` on every push to `main`, so this
step runs once, after #61 merges, using that run's SHA tag.

```
az containerapp create --resource-group rg-landmoney --name landmoney-categorizer --environment cae-landmoney --image ghcr.io/landcovschi/landmoney-categorizer:sha-<40 characters> --target-port 8000 --ingress internal --min-replicas 0 --max-replicas 1 --cpu 0.25 --memory 0.5Gi --env-vars "CATEGORIZER_PREDICTOR=rules"
```

- **`--ingress internal`.** No public FQDN, so there is nothing on the internet
  to find. This matters more than it looks: the endpoint takes unauthenticated
  POSTs, and once the model of #59 is behind it, an open endpoint is somebody
  else's Anthropic bill.
- **`--target-port 8000`**, which is what the Dockerfile's `CMD` binds and what
  `EXPOSE` declares. #35's first trap applies unchanged -- a wrong target port
  provisions successfully and then fails every probe.
- **`--cpu 0.25 --memory 0.5Gi`**, the smallest valid combination; memory has to
  be twice the vCPU count in GiB. The substring scan the rules baseline performs
  needs none of it.
- **`CATEGORIZER_PREDICTOR=rules`, written out although it is also the image's
  default.** Two reasons. It is what `az containerapp show` can then answer,
  rather than "nothing is set and the image decides". And the other value costs
  money: `model` means an `ANTHROPIC_API_KEY` as a Container Apps secret and one
  Claude call per saved transaction. That is a decision with a bill attached and
  it is not this step's.

  **That decision was taken on 2026-08-30 in #87, and this bullet is now history
  rather than the state of the world.** The categorizer runs the model; the
  commands, what it costs and the ceiling are in *Turning the model on* at the end
  of this step. What is worth keeping here is what the paragraph used to say and
  what happened to it.

  It said that flipping this variable was three things and not one -- the key as a
  secret, **a Redis cache**, and the price variables of #64 -- and that `ci.yml`
  refused a deployment that was `model` with no `CATEGORIZER_REDIS_URL`, because an
  uncached model is billed once per saved transaction. It also said, in #65's own
  words, that the honest third option was no cache at all and that **the arithmetic
  was the thing to do before provisioning anything**.

  The arithmetic was done and it removed one of the three: a call is 0.62 US cents
  and the cache is ~16 USD a month, so the cache pays for itself at ~2,600 calls a
  month against the 80-160 this application makes. So it is **two** things and not
  three, the Redis gate is gone from `ci.yml`, and what replaced it asserts the two
  that are left -- the key must be a secret reference and never a value, and `model`
  with no price configured is refused.
- **No registry credentials**, for the reason step 10 gives: the package is
  public. It is a *second* package, and #24 recorded that ghcr.io makes a new one
  private by default -- **measured here, and it was already public**, listing its
  tags to an anonymous pull token on the first try. The visibility was inherited
  rather than set by anybody, so the manual package-settings step that #24
  warned about did not exist this time. If a future package does start private
  the symptom is a revision that fails to start with `UNAUTHORIZED`, and the fix
  is the package's settings on GitHub, not a flag here:

  ```
  curl -s "https://ghcr.io/token?scope=repository:landcovschi/landmoney-categorizer:pull&service=ghcr.io"
  curl -s -H "Authorization: Bearer <token>" "https://ghcr.io/v2/landcovschi/landmoney-categorizer/tags/list"
  ```

### Point the app at it

The internal FQDN carries the environment's random middle label, so read it back
rather than typing it:

```
az containerapp show -g rg-landmoney -n landmoney-categorizer --query "properties.configuration.ingress.fqdn" -o tsv
az containerapp update -g rg-landmoney -n landmoney --set-env-vars "Categorizer__BaseUrl=https://landmoney-categorizer.internal.<label>.polandcentral.azurecontainerapps.io"
```

- **`--set-env-vars` adds and updates; `--replace-env-vars` deletes everything
  else** -- including `ConnectionStrings__Default`, which is a `secretref` and
  would take the site down. One word apart in the same help text. Confirmed by
  reading all three variables back afterwards, and **read them back with `show`
  rather than from the `update` response**: the response prints the two other
  names with no `value` at all, which reads exactly like the update having
  emptied them. It has not; `show` prints them intact.
- **`https://`, and this was written the other way round first.** The reasoning
  for `http://` reads well -- the hop never leaves the environment, and plain
  http avoids validating a certificate on a connection with no public exposure --
  and it does not work. `az containerapp create` sets `allowInsecure: false`, so
  port 80 answers with a redirect; measured on this ingress, a POST to
  `http://<internal fqdn>/categorize` is **`301 Moved Permanently`**, and
  `HttpClient` follows a 301 by re-issuing it as a **GET**, which that route
  answers `405`. Over https the same request is `200
  {"category":"transport","source":"rules"}`.

  Two things worth keeping from that. The failure would have been **another
  silent null category** -- the exact state this step exists to end, arriving
  through the fix for it -- which is why `ci.yml` asserts the scheme and not
  merely the host. And `GET /health` over http appears to work, because a
  redirected GET is still a GET: **the health check is the one probe that cannot
  reveal this.**

- **The certificate validates with nothing configured**, measured rather than
  assumed: the environment's `*.internal.<label>...` name is served with a chain
  the container already trusts, so there is no certificate to install and no
  validation to disable. Worth stating because the tempting shortcut when https
  is refused is to turn validation off, and it is not needed here.
- **Two underscores**, the same trap step 12 records for the connection string.
  One makes a key nobody reads, and the symptom is silence: the app falls back to
  the `appsettings.json` default and stores no categories, which is the state
  this whole step exists to end.
- **Why an environment variable rather than a change to `appsettings.json`.** The
  committed default is what a developer machine needs, and each of the three
  arrangements wants a different address -- `127.0.0.1:8000` from the host,
  `http://categorizer:8000` inside compose, the internal FQDN in Azure. Only the
  last one is per-deployment, so only the last one is set this way.

### How it is checked

**Not with `curl` from here.** Internal ingress means there is no public FQDN, so
a laptop cannot reach it and neither can a GitHub runner. That is the point of
the arrangement rather than an inconvenience, and it is why the checks split in
two.

**But it can be probed from inside the environment**, which is how the redirect
above was found, and it is the technique to reach for rather than concluding that
an internal service is unobservable. `az containerapp exec` runs a command in a
live replica, and the categorizer's image ships an interpreter that can make an
HTTP request -- so the probe is sent **to the internal FQDN**, not to
`localhost`, and therefore travels the same DNS and the same ingress the app
does:

```
az containerapp exec -g rg-landmoney -n landmoney-categorizer --command "python -c \"import urllib.request,json;d=json.dumps({'description':'Uber ride to the airport','amount':'42.10','currency':'EUR'}).encode();req=urllib.request.Request('https://landmoney-categorizer.internal.<label>.polandcentral.azurecontainerapps.io/categorize',data=d,headers={'Content-Type':'application/json'});r=urllib.request.urlopen(req,timeout=10);print(r.status, r.read().decode())\""
```

It needs a replica to attach to, so it works while one is running and answers
`no replicas` once the app has scaled to zero. What it does **not** prove is the
.NET client's own half -- that the aspnet image trusts the same certificate, and
that the call fits the 8-second budget. Only a save through the site shows that.

What `ci.yml` asserts on every deployment, in `Check the categorizer`: the
revision runs this commit's image, the ingress is still `external: false`, and
the app's `Categorizer__BaseUrl` is exactly the internal FQDN. Those are the
three ways this comes undone with nobody noticing.

What is checked by hand, and is #61's acceptance test:

| Check                                                    | Expected                       |
| -------------------------------------------------------- | ------------------------------ |
| Save a transaction described `Uber ride to the airport`    | the row shows `transport`      |
| `az containerapp logs show -g rg-landmoney -n landmoney --type console --tail 40` | `Categorizer suggested ... by rules`, not `Categorizer is unreachable` |
| `az containerapp update -g rg-landmoney -n landmoney-categorizer --min-replicas 0 --max-replicas 0`, then save again | the transaction is saved, with no category |

**`Uber` and not `Lidl`, which is what #61 suggested.** There is no `lidl` rule:
the baseline matches ordinary words (`supermarket`, `bakery`, `bread`) plus a
short list of merchant names, and `Lidl` is answered `{"category": null}` --
measured. A verification whose expected result is "a category" and whose input
produces `null` fails while everything works, which is the wrong way for a check
to be wrong. `docs/evals.md` records the same limit as the baseline's structural
ceiling, since one category of eleven cannot be reached by merchant-name matching
at all.

The last row is the one worth doing rather than assuming: it is #39's fallback,
which was measured against `docker compose stop` and has never been seen against
an Azure ingress. `--max-replicas 0` is how a Container App is taken out of
service without being deleted; put it back with `--max-replicas 1`.

### Rolling it back

The same shape as the app, and independent of it now -- which is the half the
sidecar would not have given:

```
az containerapp revision list -g rg-landmoney -n landmoney-categorizer --all -o table
az containerapp update -g rg-landmoney -n landmoney-categorizer --image ghcr.io/landcovschi/landmoney-categorizer:sha-<an older 40 characters>
```


### Turning the model on

**#87, 2026-08-30.** Everything above deploys the categorizer running
`CATEGORIZER_PREDICTOR=rules`, which costs nothing and scores 56.1% macro recall.
`docs/evals.md` section 7 records the model at **98.9%**, measured twice, with
zero confident errors -- so until this the repository held a number it did not
use, and every transaction saved through the site was categorised at baseline
quality while the model existed only in an eval run.

This is the step that closes that. It is a decision with a bill rather than a
configuration change, so the arithmetic comes before the commands.

#### What a call costs, measured

Four rows through `evals/score.py --predictor model` on 2026-08-30, with #64's
price variables set to the published rate, which is what puts `cost_usd` on the
line at all:

```
model_call outcome=answered model=claude-opus-5 effort=low elapsed_ms=2307 input_tokens=1174 output_tokens=11 cost_usd=0.006145
model_call outcome=answered model=claude-opus-5 effort=low elapsed_ms=2075 input_tokens=1172 output_tokens=12 cost_usd=0.006160
model_call outcome=answered model=claude-opus-5 effort=low elapsed_ms=2076 input_tokens=1176 output_tokens=13 cost_usd=0.006205
model_call outcome=answered model=claude-opus-5 effort=low elapsed_ms=2046 input_tokens=1173 output_tokens=11 cost_usd=0.006140
```

**One call is 0.62 US cents.** Two things in those numbers are worth more than
the total, because both contradict what the code assumes about itself.

**Output is eleven to thirteen tokens.** Adaptive thinking at `effort=low` writes
essentially nothing on a one-word classification against a rubric supplied in
full. `anthropic_predictor.py` sets `max_tokens=2048` and its comment explains
that the ~256 a classification suggests would "truncate mid-thought and cost a
retry" -- that reasoning is sound and the headroom it reserves is never touched.
So the answer is **2.5% of the bill and the prompt is the other 97.5%**, which
inverts every intuition about where to look if this ever needs to be cheaper.
`CATEGORIZER_EFFORT` is a latency and quality lever; it is not a cost lever.

**Input is ~1,173 tokens where the prompt text measures ~700.** The remaining
~450 is `RESPONSE_SCHEMA` and the message framing, neither of which appears in
`prompt.py`'s character count. Anything that prices this by measuring the file
will be low by 60%.

#### What a saved transaction costs, which is not one call

Since #67 the categorizer is asked twice for one transaction: once 400 ms after
the typing stops, for the badge under the description field, and once again when
the row is saved -- because the save deliberately does not trust the browser's
answer, since a client that can send a category can send a source. Every
`(description, amount, currency)` that survives the debounce is a call, and the
amount is usually typed after the description, so a transaction entered
comfortably is **two to four calls, 1.2 to 2.5 US cents**.

At forty transactions a month -- one person, weekly, which is what this
application is -- that is **50 to 100 US cents a month**. A month of nothing but
CSV imports costs nothing at all: #62 does not call the categorizer.

#### The cache, and why there is not one

`ci.yml` used to refuse a deployment that was `model` with no
`CATEGORIZER_REDIS_URL`. #65 wrote that gate and, in the same breath, wrote down
what would settle whether it was right:

> The honest third option is no cache at all and a per-call bill, which for
> weekly use may simply be smaller than 16 USD -- **that arithmetic is the thing
> to do before provisioning anything.**

Done, and it is not close. **Azure Cache for Redis Basic C0 is ~16 USD a month**
-- the figure #65 recorded on 2026-08-29, carried forward here and still to be
re-read rather than trusted -- which at 0.62 cents a call pays for itself at
**~2,600 calls a month, 87 every day, every day**. This application makes 80 to
160 a month. The gate was refusing the cheaper of two states by a factor of about
thirty, so it is **gone rather than satisfied**, and what replaces it is below.

The alternative #65 named loses the same way and by arithmetic rather than by a
price list. A `redis:8-alpine` container app of its own needs `--min-replicas 1`,
because a cache that scales to zero is a cache that is always cold; the smallest
valid size is 0.25 vCPU and 0.5 GiB, which running continuously is ~648,000
vCPU-seconds and ~1.3 million GiB-seconds a month. After the subscription's
monthly free grant that is roughly 14 USD at the published consumption rates --
and the grant is already partly spent by the two apps that exist, so 14 is a
floor. The order of magnitude is what decides this, and it is the same one.

**What is given up by having no cache**, said plainly rather than waved away: the
preview and the save of the same transaction are two calls where one would do, so
roughly a third to a half of the bill above is a duplicate. A third of a dollar a
month is not worth sixteen.

`docker-compose.yml` keeps its Redis and nothing changes locally -- the cache is
still where an experiment that re-runs the same descriptions belongs, and #65's
measurements stand. What changed is the answer to "does the deployed one need
one", and it is no.

**The cache that would actually pay, and it is not this one.** Anthropic's prompt
caching would cut the ~1,150-token constant prefix -- the system prompt and the
schema, byte-identical on every call -- to a tenth on a read, which is where 97%
of this bill is. It needs no infrastructure and no monthly charge, only a
`cache_control` on one content block, and `_user_message`'s docstring already
keeps the varying half at the end for exactly that day. `Prices.cost_of` says in
as many words that the figure it computes becomes an overestimate when it
happens. It is a change to the adapter, so it is its own issue and not this one.

#### The ceiling, if anything ever loops

Nothing in this deployment caps spend, and it is worth writing down what that
means rather than discovering it.

`--max-replicas 1` bounds concurrency and not money. `/categorize` is a `def`
handler, so Starlette dispatches it to AnyIO's thread pool -- forty threads by
default -- and at ~2.1 s a call that is about **19 calls a second, or ~7 USD a
minute, ~420 USD an hour**. Nothing rate-limits the preview endpoint either; #67
says so in as many words, and the only thing between that screen and an unbounded
number of calls is that a person types slowly.

**So the ceiling is set at Anthropic, in the Console, and not here.** Put the key
in a workspace with a **monthly spend limit** -- 5 USD is roughly 800 calls,
which is five times the expected use and a rounding error against the numbers
above. That is the owner's act, it costs nothing, and it is the only control in
this whole arrangement that a bug in this repository cannot defeat.

What makes it the right shape rather than merely the available one: **the limit
degrades into the state the application already handles.** A call refused for
spend raises inside `AnthropicPredictor`, which catches `Exception`, logs the
traceback, writes `model_call outcome=failed`, and returns no category -- #39's
fallback, unchanged by a model being behind the port. The site keeps saving
transactions. A spend limit here does not take anything down.

#### The commands

Three of them, in this order, and the order matters: a revision that references a
secret which does not exist yet does not start.

**One.** The key as a secret, the same road the connection string takes (step 12).
It is read rather than typed into the command, because a command line reaches the
shell's history file and this one is a credential:

```
read -rs ANTHROPIC_KEY
echo "length: ${#ANTHROPIC_KEY}"
az containerapp secret set -g rg-landmoney -n landmoney-categorizer --secrets "anthropic-key=$ANTHROPIC_KEY"
unset ANTHROPIC_KEY
```

**Checking a key by printing its length and never its value** is the rule from
#76, and it is the whole of what can be confirmed here: `secret list` without
`--show-values` answers with names only, which is the property worth keeping
rather than a limitation to work around.

**Two.** The predictor, the key reference, and the prices -- one `update`, so one
new revision:

```
az containerapp update -g rg-landmoney -n landmoney-categorizer --set-env-vars "CATEGORIZER_PREDICTOR=model" "ANTHROPIC_API_KEY=secretref:anthropic-key" "CATEGORIZER_PRICE_INPUT_PER_MTOK=5.00" "CATEGORIZER_PRICE_OUTPUT_PER_MTOK=25.00"
```

- **`secretref:anthropic-key`, never the key itself.** `--set-env-vars
  "ANTHROPIC_API_KEY=sk-..."` deploys, starts and answers correctly, and the key
  is then readable by anybody who can run `az containerapp show` -- and it stays
  in that revision's template for as long as the revision is listed, which
  outlives rotating the key, because rotating writes a new revision and does not
  edit the old one. `ci.yml` asserts the difference; see below.
- **`--set-env-vars` adds and updates; `--replace-env-vars` removes everything
  else.** Step 12's warning, one app along, and here the variable it would delete
  is `CATEGORIZER_PREDICTOR` -- which reads as unset, which means `rules`, which
  is a working deployment quietly serving the baseline.
- **The two prices are the published rate for `claude-opus-5` on 2026-08-30, and
  they are configuration because a rate moves without this repository noticing**
  (#64). Re-read them when the model changes; a stale figure in a log is worse
  than an absent one, because it is believed.
- **`CATEGORIZER_REDIS_URL` is deliberately not set**, per the arithmetic above.
  `cache.py` says so on its own at start-up: `CATEGORIZER_REDIS_URL is not set, so
  answers are not cached and every call is billed.`

**Three.** Nothing. There is no third command -- the app needs no change at all.
`Categorizer__BaseUrl` still points at the same internal FQDN, and the .NET side
has never known which predictor is behind it.

#### How it is checked

**The first check is the log, not the 200**, and that is #87's first trap.
`anthropic.Anthropic()` constructs cleanly with no credential anywhere and defers
the failure to the first request, so a deployment that selects the model and
forgets the key **starts, serves 200s, and answers `category: null` for ever**.
From the .NET side that is an *abstention*: counted, not logged (#64), and
indistinguishable from a model declining every row. The only signal in the
running system is one line in the other container's log stream.

```
az containerapp logs show -g rg-landmoney -n landmoney-categorizer --type console --tail 40
```

| What it says                                              | What it means                          |
| --------------------------------------------------------- | -------------------------------------- |
| `Categorising with the model. This costs money per request.` | `CATEGORIZER_PREDICTOR=model` took     |
| `ANTHROPIC_API_KEY is not set...`                          | the secret reference did not arrive     |
| `model_call outcome=answered ... cost_usd=0.006...`         | a real call, priced                     |
| `model_call outcome=failed ...`                            | the call raised -- key, quota or network |
| `cost_usd=unpriced`                                        | the price variables did not arrive      |

Then the acceptance test, which is a save through the site:

| Check                                                     | Expected                          |
| --------------------------------------------------------- | --------------------------------- |
| Type `haircut` into the description field and wait         | the badge suggests `other`        |
| Save it                                                    | the row shows `other`, badge `model` |
| `select category, category_source from transactions order by created_at desc limit 1` | `other`, `model`  |

**`haircut` and not `Uber ride to the airport`**, which is what the rules check
above uses, and the difference is the point rather than the variety. A
description both predictors answer identically would show a category and prove
nothing about which one produced it. `other` is the category `docs/evals.md`
records as structurally unreachable by substring matching -- one of eleven, which
is the baseline's 90.9% hard ceiling -- and the model took it from 0/3 to 3/3.

Both halves of that were checked on 2026-08-30 before being written here, which is
the mistake #61 and #67 each made once by writing an acceptance test whose input
produces the failure it is meant to detect:

```
rules: line 2  other -> unknown  haircut
model: model_call outcome=answered ... input_tokens=1173 output_tokens=10 cost_usd=0.006115   -> other
```

And the third of #87's verifications, which is the one worth actually doing:
**revoke the key in the Anthropic Console and save another transaction.** The
transaction is stored, with no category, and `landmoney-categorizer` logs
`model_call outcome=failed` with a traceback. That is #39's fallback under a
model rather than under a stopped container, and it has never been seen in that
configuration.

What `ci.yml` now asserts on every deployment, which replaces #65's Redis gate:

- `ANTHROPIC_API_KEY` is a `secretRef` and never a literal value -- checked for
  both predictors, because the wrong one of those is a leak whichever predictor is
  running. The check **counts rather than fetches**: `length()` over a filter
  answers `0` or `1`, where asking for `.value` would pull the key into a runner
  variable on the very run that is reporting it exposed. The filter has to exclude
  the empty string as well as null, because a variable filled from a secret is
  returned by `show` with `value` present and empty -- which is step 12's
  acceptance test, and would otherwise count every correct deployment as a leak;
- `model` with no key secret is refused, which turns the trap above from a
  sentence telling somebody to read a log into a red step;
- `model` with no price configured is refused, because a deployment that bills
  while every line says `cost_usd=unpriced` is exactly the shape this project
  keeps writing down -- a dependency whose absence nothing reports.

#### Turning it back off

One command, and it is the reason `CATEGORIZER_PREDICTOR` is written out
explicitly rather than defaulted:

```
az containerapp update -g rg-landmoney -n landmoney-categorizer --set-env-vars "CATEGORIZER_PREDICTOR=rules"
```

The secret stays and costs nothing; the rules path never reads it. Billing stops
with the revision, and the site is a baseline categorizer again with no other
change anywhere.

**Removing the key entirely** is `az containerapp secret remove -g rg-landmoney -n
landmoney-categorizer --secret-names anthropic-key`, and it must not be run while
a revision still references it. Flip the predictor first.


## Step 17 -- reading what the categorizer is doing

Added 2026-08-29 with #64. Nothing to create and nothing to set: this is where the
numbers are, now that there are numbers.

Two kinds of line reach Log Analytics, both as JSON since #64 -- one row per entry,
with the message template's placeholders as fields under `State`:

* one per call worth a line, from `LandMoney.Web.Categorizing.CategorizerClient`,
  carrying `Outcome` -- `suggested`, `refused`, `timeout`, `unreachable`,
  `unreadable` or `unusable`. **An abstention writes no line and is only a
  count**: it is the baseline declining on about a third of the labelled set, and
  a warning per save would train a reader to skip the ones that matter;
* one per minute in which anything happened, from `CategorizerSummary`, carrying
  every count and `P50Ms` / `P95Ms` / `MaxMs`. **Silence means nothing was saved**,
  not that the summary is broken.

The whole point of the outcome being a field rather than a sentence is that this
is a query and not a search:

```
az monitor log-analytics query \
  --workspace <the workspace id from step 8> \
  --analytics-query "ContainerAppConsoleLogs_CL | where ContainerName_s == 'landmoney' | extend p = parse_json(Log_s) | where tostring(p.Category) endswith 'CategorizerClient' | summarize count() by tostring(p.State.Outcome)" \
  -o table
```

`not-configured` on that list is the bug #61 existed to fix, and it is the one to
look for first: it means `Categorizer__BaseUrl` is not reaching the app and every
transaction is being stored with no category. `timeout` and `unreachable` are not
the same thing and are not interchangeable -- a stopped container leaves the SYN
unanswered, so it counts as a timeout (#39, measured twice).

**Tokens and cost live in the other container**, because only it can see them.
`landmoney-categorizer` writes one `model_call outcome=... elapsed_ms=...
input_tokens=... output_tokens=... cost_usd=...` line per call, and it says
`cost_usd=unpriced` unless `CATEGORIZER_PRICE_INPUT_PER_MTOK` and
`CATEGORIZER_PRICE_OUTPUT_PER_MTOK` are both set on that app -- there is no price
in the code, deliberately, because a stale one would be believed. **Since #87
those lines are there**: the categorizer runs the model and both prices are set,
which `ci.yml` refuses to deploy without. Summing `cost_usd` over a month is the
bill, and it is the only place in this system that can be asked what the model
spent.

The line to look for **first** is `outcome=failed`. A revoked, expired or
spend-limited key raises inside the adapter, is logged here with its traceback,
and reaches the .NET side as a **200 with no category** -- which #64 counts as an
abstention and does not log, because an abstention is a normal answer. So a key
that has stopped working looks, from the application's own logs, exactly like a
model declining every row. This container is the only witness.

**There is no `cache` line in production, and its absence is correct** -- #87.
`CATEGORIZER_REDIS_URL` is unset on the deployed categorizer, so `cache.py` says
so once at start-up and every call is billed on purpose:

    CATEGORIZER_REDIS_URL is not set, so answers are not cached and every call is billed.

That line appearing is the deployment working as decided; a `cache` line appearing
would mean somebody provisioned a Redis without the arithmetic in step 16. The
query below is for a local session with the compose stack, where the cache does
run and where #65's `outcome=hit|miss` and `hit_rate=` are worth reading:

    ContainerAppConsoleLogs_CL
    | where ContainerName_s == 'landmoney-categorizer' and Log_s startswith 'cache '

Which side to believe when they disagree, decided in #64: **this service is
authoritative for what the model did, and the .NET app for what the user got.** A
call answered at seven seconds is billed and logged here, and is a `timeout`
over there; both are correct, and they are answering different questions.

## Tearing it all down

One command, and it is the reason everything went into one resource group:

```
az group delete --name rg-landmoney --yes --no-wait
```

This deletes the database and its backups. There is nothing else to clean up --
the Log Analytics workspace is inside the group too.

**Two things do not come back the way they went, and both are step 15's.** The key
vault is soft-deleted rather than gone, and it holds its globally unique name for
the seven days `--retention-days` was set to -- so rebuilding inside that window
needs `az keyvault recover` or a different name. And the container app's
system-assigned identity is deleted with the app, so a rebuilt app has a **new**
principal id and both role assignments have to be made again. Neither is an error
anybody would guess from: the first fails at `keyvault create` with a name
conflict, and the second starts cleanly and then refuses to serve, because
`VerifyKeyRing` reads the ring at startup and a 403 is not something it carries
on from.
