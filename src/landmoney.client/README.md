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

Everything under `src/` is still the scaffolder's demo page. The form and the
list replace it in #6.
