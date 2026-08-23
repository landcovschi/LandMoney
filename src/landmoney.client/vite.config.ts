import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],

  // Development only. Two servers run side by side -- Vite on 5173 serving the
  // client, the .NET app on 5150 serving the API -- so a fetch("/api/...") from
  // the Vite-served page would go to 5173, where there is no API. Forwarding it
  // here means the browser only ever sees one origin, and there is no
  // cross-origin request left for the API to permit.
  //
  // CORS on the .NET side was the alternative and lost in #4: it puts a
  // permission into the API that exists purely because of how development is
  // arranged, and that kind of permission survives into production by accident.
  //
  // None of this applies to a production build. The files in dist/ are served
  // by the .NET app itself, from the same origin as the API.
  server: {
    proxy: {
      // The key is matched as a prefix rather than an exact path, so this one
      // entry covers /api/transactions and everything added beside it later.
      // The value has a longer object form -- target, rewrite, changeOrigin,
      // secure, configure -- and none of those are needed: the route is
      // literally /api/transactions on both sides, so there is no path to
      // rewrite, and Kestrel on localhost does not inspect the Host header.
      //
      // The target is the HTTP port deliberately. Both launch profiles publish
      // 5150, so either one works and there is nothing to remember at start-up.
      //
      // That is true only because UseHttpsRedirection is gated to non-Development
      // in Program.cs. It used to run unconditionally, and then the https profile
      // answered 5150 with a 307 to 7063 -- which this proxy does not follow. It
      // hands the 307 to the browser, which makes exactly the cross-origin
      // request the proxy exists to avoid, against a self-signed certificate.
      // The workaround was --launch-profile http every time, and Visual Studio's
      // run dropdown could not be told about it.
      '/api': 'http://localhost:5150',
    },
  },
})
