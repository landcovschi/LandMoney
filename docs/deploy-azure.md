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
summary. **This is the step #38 exists to delete**, and doing it by hand once is
the point.

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
run that built the commit being deployed:

```
gh run download --repo landcovschi/LandMoney -n efbundle -D .
```

**It will not run on this machine, and that is deliberate.** It is a linux-x64
ELF binary; Windows answers with a format error rather than anything helpful.
Run it in the smallest image that can host it -- the same base the application's
own runtime image is built on, minus ASP.NET:

```
docker run --rm -v "${PWD}:/w" -w /w -e ConnectionStrings__Default="$pgConn" mcr.microsoft.com/dotnet/runtime-deps:10.0 sh -c "chmod +x ./efbundle && ./efbundle"
```

Two things in that line are load-bearing:

- **`chmod +x`.** A GitHub artifact is a zip, and a zip does not carry the
  executable bit. Without it the answer is `Permission denied` on a file that is
  plainly sitting there.
- **`runtime-deps`, not `runtime` or `aspnet`.** `--self-contained` bundles the
  .NET runtime but not glibc, ICU and OpenSSL, which is what that image is.

From Git Bash the volume argument needs `MSYS_NO_PATHCONV=1` in front of the
whole command, or the shell rewrites `/w` into a Windows path and docker
answers `the working directory 'W:/' is invalid`.

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

### Running it twice

The second run of the same bundle, unchanged:

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

**Both of these are hand steps today, and #38 is what joins them.** There the
bundle is downloaded from the same run that built the image, so the two cannot
disagree about which commit is being deployed -- which is the argument for
building it in `build` rather than in a job of its own.

**One thing #38 has to measure rather than assume:** whether a GitHub-hosted
runner can reach the database at all. The firewall is the 0.0.0.0 "all Azure
services" rule from step 5 plus this machine's address, and whether a runner's
outbound address falls inside the first is not something this file can answer.
If it does not, the shape is a temporary firewall rule created and removed by
the deploy job, which the OIDC login #38 already needs makes possible.

## Tearing it all down

One command, and it is the reason everything went into one resource group:

```
az group delete --name rg-landmoney --yes --no-wait
```

This deletes the database and its backups. There is nothing else to clean up --
the Log Analytics workspace is inside the group too.
