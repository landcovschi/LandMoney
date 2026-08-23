# LandMoney client

React and TypeScript, built by Vite. In production these files are served by the
.NET app from `wwwroot`, on the same origin as the API. In development they are
served by Vite on its own port, and `/api` is forwarded to the API by the dev
proxy in `vite.config.ts`.

There is no `dist/`. `build.outDir` points at `../LandMoney.Web/wwwroot`, so
`npm run build` writes straight into the folder the .NET app serves, with no
copy step to run and none to forget -- see #20 and the comment in
`vite.config.ts` for what that costs.

## Requirements

Node 24. The number lives in `.nvmrc`, so `nvm use` and `fnm use` pick it up
without being told. `engine-strict=true` in `.npmrc` makes npm refuse to install
on a different major version rather than warn and continue.

## Running it

The API has to be up first. Either launch profile will do, from a terminal or
from Visual Studio with the debugger attached -- both publish 5150, which is
where the proxy looks.

```powershell
dotnet run --project ..\LandMoney.Web
```

Then, from this folder:

```powershell
npm ci
npm run dev
```

The client is on `http://localhost:5173`, and a `fetch("/api/transactions")`
from it reaches the API through the proxy.

### Why the profile stopped mattering

It used to have to be `--launch-profile http`, and Visual Studio's run dropdown
could not be told so: it prefers `https` for a project that has one, F5 broke the
client, and what appeared on screen was a CORS error naming neither the profile
nor the cause.

The cause was `UseHttpsRedirection` running in Development, where it answered
port 5150 with a 307 to 7063 -- and this proxy does not follow redirects, it
hands the 307 to the browser, which then makes the cross-origin request the proxy
exists to prevent. `Program.cs` now gates that line to non-Development, beside
`UseHsts`, which was already gated for the same reason. Both exist to keep real
traffic off the clear, and development has no real traffic.

### Check the port, not the fact that it started

If the screen says the API is unreachable while the API is plainly running, it is
almost always listening somewhere else. The startup line has to say:

```
Now listening on: http://localhost:5150
```

**`5000` means `launchSettings.json` was never read.** That happens when
`bin\Debug\net10.0\LandMoney.Web.exe` is started by hand or from Explorer:
`launchSettings.json` belongs to the tooling -- `dotnet run` and the IDE read it
and pass what it says to the app through environment variables -- and the app
itself has never heard of the file. With no profile there is no `applicationUrl`,
so Kestrel falls back to its own default. Nothing is wrong with the API when this
happens; it is up and answering, just not where the proxy is looking, and there
is no redirect to notice either.

This is also why a Visual Studio session shows the bare exe in its command line
and still listens on 5150 -- Visual Studio launches the exe and applies the
profile itself.

Which it did only halfway once, on 2026-08-23: `ASPNETCORE_ENVIRONMENT` arrived
and `applicationUrl` did not, so the app ran in Development on port 5000.
`applicationUrl` is the tooling's spelling of `ASPNETCORE_URLS`, and it is the
translation step that failed. Both profiles now name the port a second time
under `environmentVariables`, in the form the app reads directly, so there is
nothing left to translate.

`launchBrowser` stays on, and this sentence used to claim the opposite -- it was
never true of `launchSettings.json`, where both profiles have always had it set.
The complaint behind it was real: F5 opened a browser on the API port and the
API had no screen there, only the leftover Razor page. #20 is what makes the
setting right rather than wrong, because the API port is now where the client
is. It is still the wrong port for working *on* the client -- that is 5173, with
hot reload -- and the page F5 opens is whatever `npm run build` last produced.

## Running the whole thing without Vite

`npm run build` once, then `dotnet run` on its own -- no dev server anywhere:

```powershell
npm run build
dotnet run --project ..\LandMoney.Web
```

`http://localhost:5150` is then the client and the API on one origin, which is
how it is deployed. This is worth doing before opening a pull request: it is the
only arrangement that exercises `UseStaticFiles`, the fallback and the cache
headers, and the dev server exercises none of them.

Two things it will show that `npm run dev` never does. A stale build is served
silently -- `wwwroot` holds whatever the last `npm run build` produced, and
nothing reports its age. And `/` answers 404 in a clone where the client has not
been built yet: the API is fine and still answers, there is simply no
`index.html`, and that is the intended answer rather than a fault to work
around.

### Deleting `wwwroot` after a build stops the app from starting

Not the same thing as never having built it, and it fails much louder. The SDK
records the folder in a static web assets manifest while compiling, and the
manifest is read during `WebApplication.CreateBuilder` -- so a folder that was
there at build time and is gone at run time throws before a single line of
`Program.cs` executes:

```
Unhandled exception. System.IO.DirectoryNotFoundException: D:\Work Home\LandMoney\src\LandMoney.Web\wwwroot\
   at Microsoft.Extensions.FileProviders.PhysicalFileProvider..ctor(String root, ExclusionFilters filters)
   at Microsoft.AspNetCore.Hosting.StaticWebAssets.StaticWebAssetsLoader.UseStaticWebAssetsCore(...)
   at Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(String[] args)
```

Nothing in that names the client, and `dotnet build` will not fix it on its own
because the manifest is only regenerated when the build is not incremental.
Either put the folder back with `npm run build`, or delete `obj/` and `bin/` so
the next build writes a manifest that does not mention it.

## Scripts

| Command | What it does |
|---|---|
| `npm run dev` | dev server on 5173, with HMR and the `/api` proxy |
| `npm run build` | type-check with `tsc -b`, then bundle into `../LandMoney.Web/wwwroot`, emptying it first |
| `npm run lint` | oxlint |
| `npm run preview` | serve the build output locally. Of limited use now that the .NET app serves the same folder, and with no `/api` proxy behind it |

`vite` strips TypeScript types without checking them, which is why `build` runs
`tsc -b` first and why a type error does not stop `npm run dev`.

## What is in `src/`

One screen, added in #6: a form that adds a transaction and a list of what has
been spent.

| Path | What it is |
|---|---|
| `api/types.ts` | the contract, as TypeScript. Matches `Api/TransactionContracts.cs` |
| `api/transactions.ts` | the two calls, with a timeout and one error type |
| `components/TransactionForm.tsx` | the form, and the server's messages beside their fields |
| `components/TransactionList.tsx` | the table, and the loading, failed and empty states |
| `App.tsx` | owns the list state and wires the two together |

Three things about it that are decisions rather than style, each written down
next to the code that depends on it:

- **`occurredAt` stays a string all the way to the screen.** It arrives as
  `"2026-08-19"`, and `new Date("2026-08-19")` is UTC midnight -- so anywhere
  west of UTC, making it prettier renders the day before. `createdAt` is an
  instant and *is* converted, which is the same rule pointing the other way.
- **Bounds are not validated here.** `required`, `step` and `maxLength` are,
  because the browser enforces them for free. Five years back, one day ahead and
  the ceiling of `numeric(18,2)` are policy, they live on
  `CreateTransactionRequest`, and a copy in TypeScript is a second number that
  has to change with the first and will not. The 400 comes back keyed by field
  and the message is shown beside its own input.
- **Amounts are formatted, never summed.** The column mixes currencies, so a
  total over all of them is a number that means nothing. Formatting is pinned to
  two decimal places rather than left to the currency's own minor unit -- the
  yen has none, and an amount stored as 12.34 would otherwise be *displayed* as
  12.

### If the list says it cannot reach the API

That message is usually right and usually means the API is not running, or is
running on the `https` profile. Note what it took to say so: the dev proxy
catches the refused connection itself and answers **502**, so the browser's
`fetch` succeeds and there is no network error to detect. `api/transactions.ts`
treats 502 and 504 as unreachable for that reason.
