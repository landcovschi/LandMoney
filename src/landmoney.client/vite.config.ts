import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],

  // Where a production build lands: straight into the .NET project's wwwroot,
  // which is the folder Program.cs serves. There is no dist/ any more and no
  // copy step anywhere -- `npm run build` puts the files where they are read
  // from, and it does the same thing on this machine and on a CI runner.
  //
  // That sameness is the whole argument. The alternative was Vite's default
  // dist/ plus a copy, and a copy that only exists in the CI workflow is a step
  // that local development never performs: `dotnet run` would serve whatever
  // was copied last, with nothing anywhere reporting that it is stale. The
  // price paid for avoiding that is real and is two things. The client's
  // config now knows the server project's folder layout, so moving either
  // folder breaks a build rather than a reference. And when the client
  // eventually gets its own nginx container -- the arrangement CLAUDE.md
  // expects once the Python service makes it several containers anyway --
  // this path is what has to be undone.
  //
  // emptyOutDir is required rather than chosen: outDir is outside Vite's
  // project root, and Vite refuses to delete anything out there unless told to
  // in so many words. Without it every build leaves the previous build's
  // hashed files behind, and they would be published into the image forever.
  build: {
    outDir: '../LandMoney.Web/wwwroot',
    emptyOutDir: true,
  },

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
