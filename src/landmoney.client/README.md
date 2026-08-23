# LandMoney client

React and TypeScript, built by Vite. In production these files are served by the
.NET app from `wwwroot`, on the same origin as the API. In development they are
served by Vite on its own port, and `/api` is forwarded to the API by the dev
proxy in `vite.config.ts`.

## Requirements

Node 24. The number lives in `.nvmrc`, so `nvm use` and `fnm use` pick it up
without being told. `engine-strict=true` in `.npmrc` makes npm refuse to install
on a different major version rather than warn and continue.

## Running it

The API has to be up first, and it has to be started **on the `http` profile**:

```powershell
dotnet run --project ..\LandMoney.Web --launch-profile http
```

Then, from this folder:

```powershell
npm ci
npm run dev
```

The client is on `http://localhost:5173`, and a `fetch("/api/transactions")`
from it reaches the API through the proxy.

### The profile is not optional

The `https` profile publishes 7063 as well, and `UseHttpsRedirection` then
answers port 5150 with a 307 to it. The dev proxy does not follow redirects: it
hands the 307 to the browser, which makes a cross-origin request against a
self-signed certificate. What you see is a CORS error naming neither the profile
nor the redirect.

`dotnet run` with no arguments is safe, because `http` is the first profile in
`launchSettings.json`. Visual Studio's run dropdown is not -- it defaults to
`https` for a project that has one.

## Scripts

| Command | What it does |
|---|---|
| `npm run dev` | dev server on 5173, with HMR and the `/api` proxy |
| `npm run build` | type-check with `tsc -b`, then bundle into `dist/` |
| `npm run lint` | oxlint |
| `npm run preview` | serve the built `dist/` locally |

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
