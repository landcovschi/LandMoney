using LandMoney.Web.Api;            // MapTransactionEndpoints
using LandMoney.Web.Data;          // AppDbContext
using Microsoft.AspNetCore.HttpOverrides; // ForwardedHeaders
using Microsoft.EntityFrameworkCore; // UseNpgsql

var builder = WebApplication.CreateBuilder(args);

// AddProblemDetails, and no AddControllersWithViews above it. The MVC services
// went with the Razor pages they existed for; what is left is minimal APIs,
// which need nothing registered.
//
// This is what UseExceptionHandler below writes when something throws: an RFC
// 9457 body, the same shape ValidationFilter<T> already answers a bad request
// with. Without it the handler has no formatter and an unhandled exception
// leaves an empty 500 -- a status with nothing in it, where the client's
// readProblem is looking for something to show.
//
// Worth knowing what else it turns on, because it is easy to mistake for a bug
// later: with this registered, minimal APIs also give the model binder's own
// 400 a body. Until now a request missing a `required` member got a bare 400,
// which api/transactions.ts has a branch for.
builder.Services.AddProblemDetails();

// TimeProvider is not registered by the framework. Measured rather than assumed
// while writing #21: a default WebApplication answers null for it.
//
// The registration exists so that production and the tests walk the same path.
// PlausibleDateAttribute finds its clock with
// validationContext.GetService(typeof(TimeProvider)) -- the only door an
// attribute has -- and ValidationFilter<T> hands it this container to ask. With
// nothing registered the lookup always misses and the attribute always takes its
// fallback, so the lookup would be a line only tests ever exercise. That is the
// shape of code that works until the day it matters.
//
// Nothing about today's behaviour changes: the fallback is TimeProvider.System,
// which is what is being registered. What changes is that swapping the clock is
// now one line here rather than an edit to the attribute.
//
// Which is exactly why NO TEST PROTECTS THIS LINE, and deleting it leaves the
// suite green -- measured in review of #31, 49 passed. That is a property of the
// line, not a gap in the tests: every test builds its own ServiceCollection and
// cannot see this container, and production cannot tell the difference either,
// because the fallback is the same object. No observation anywhere distinguishes
// this line from its absence.
//
// So it is kept alive by this comment and nothing else. If coverage ever points
// at it and finds nothing, that is the expected reading, not a licence to remove
// it: taking it out puts the service-locator branch back to being exercised by
// tests alone. The way to make it load-bearing would be to drop the fallback in
// PlausibleDateAttribute, and that costs more than it buys -- the fallback is
// what keeps the attribute usable from a bare Validator.TryValidateObject, which
// PlausibleDateAttributeTests covers on purpose.
builder.Services.AddSingleton(TimeProvider.System);

// GetConnectionString returns null when the key is missing or misspelled, and
// UseNpgsql accepts null without complaint -- the application would then start
// happily and fail at the first query with an error about the connection rather
// than about the configuration. Fail here instead, where the message can name
// the actual cause.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default is not set. Set it with: "
        + "dotnet user-secrets set \"ConnectionStrings:Default\" \"<connection string>\"");

// UseSnakeCaseNamingConvention rewrites every table, column, key and index name
// from the model, so "Transactions" becomes transactions. Postgres folds an
// unquoted identifier to lower case, which means a PascalCase name is a quoted
// identifier forever and every hand-written query has to quote it too. This
// affects the schema only -- the C# property names are untouched.
builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    // First in this branch, because both lines below ask the request which
    // scheme it arrived on, and inside a container the honest answer is http:
    // Container Apps terminates TLS at its ingress and speaks plain HTTP to
    // port 8080. So both were doing nothing, and only one of them said so --
    // UseHttpsRedirection logs "Failed to determine the https port for
    // redirect" at every start (#23 predicted it, #35 saw it), while
    // HstsMiddleware returns early on !Request.IsHttps and writes nothing at
    // all. Measured on the deployed app before this line existed: the https
    // response carried no Strict-Transport-Security header. A security header
    // that is silently absent is worse than one deliberately left out.
    //
    // XForwardedProto and nothing else. XForwardedFor is what every example
    // pairs it with, and it stays out on purpose: it would set RemoteIpAddress
    // from a header, nothing here logs or rate-limits by address, and a
    // spoofable client IP is a liability the day something does.
    //
    // Clearing the two lists is required rather than tidy, and forgetting it
    // is silent. The defaults trust exactly one proxy -- the loopback address
    // -- and the ingress is a different pod on the environment's network, so
    // with the defaults left in place the header is read, judged untrusted and
    // dropped: the site behaves identically and nothing is logged. It cannot be
    // done in the object initializer above, either, because both properties are
    // get-only lists and `KnownIPNetworks = { }` adds nothing rather than
    // removing anything.
    //
    // That claim was checked by removing these two lines and rebuilding, the
    // way #21 checked the test suite, and the check has a trap of its own worth
    // more than the result. Against `localhost` the mutation changes nothing --
    // the request comes from the one address the defaults already trust, so
    // HSTS appears either way. It has to be sent to this machine's LAN address
    // instead, at which point the mutated build drops the header and the real
    // one keeps it. Four requests, and only one of the four distinguishes the
    // two builds:
    //
    //   cleared,  from 172.28.x.x, X-Forwarded-Proto: https  ->  HSTS present
    //   cleared,  from 172.28.x.x, no header                 ->  HSTS absent
    //   defaults, from 172.28.x.x, X-Forwarded-Proto: https  ->  HSTS ABSENT
    //   defaults, from localhost,  X-Forwarded-Proto: https  ->  HSTS present
    //
    // The last row is the one to remember: a proxy-trust bug cannot be
    // reproduced from the machine running the process.
    //
    // Confirmed on the deployed app once an image carrying this shipped:
    // `strict-transport-security: max-age=2592000` on revision 0000002, where
    // the revision before it sent no such header. That is also the only
    // available proof that the ingress sends X-Forwarded-Proto at all -- nothing
    // here echoes a request header back, so the emitted HSTS header is the
    // measurement.
    //
    // It is `KnownIPNetworks`, not the `KnownNetworks` every example still
    // shows -- that one is `[Obsolete]` here and the build says so
    // (ASPDEPR005), because it is typed on
    // Microsoft.AspNetCore.HttpOverrides.IPNetwork and the framework has moved
    // to System.Net.IPNetwork. This is the compiler catching what #22 and #24
    // had to catch by hand for GitHub Action majors, which is the cheap version
    // of that lesson.
    //
    // What clearing them costs: this process now believes any X-Forwarded-Proto
    // it is handed. Port 8080 is reachable only from inside the environment --
    // that part is structural, the ingress being the only route in -- so a
    // spoofer has to be past the ingress already, at which point the scheme
    // this process believes in is not the interesting problem. The usual second
    // reassurance is that the proxy overwrites a client-supplied header rather
    // than appending to it, which is Envoy's documented behaviour and is NOT
    // something measured here: nothing in this application echoes a request
    // header back, so there was nothing to read it from. Believed, not checked.
    //
    // What this does NOT fix: the hop from the ingress to here is still plain
    // HTTP. Nothing in this file can change that; it is what "TLS terminated at
    // the edge" means everywhere.
    //
    // What lost, #36: deleting UseHsts and UseHttpsRedirection outright and
    // recording that TLS enforcement lives at the ingress -- which is true, the
    // ingress answers http with a 301 of its own, measured, and that response
    // carries no `server: Kestrel` header, which is how it is known to come
    // from Envoy rather than from this process. It lost on making the
    // application depend on a property of one host: behind the nginx container
    // CLAUDE.md already expects, or under plain docker compose, both would be
    // gone again with nothing reporting it.
    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedProto,
    };
    forwardedHeadersOptions.KnownIPNetworks.Clear();
    forwardedHeadersOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeadersOptions);

    // No path argument any more. UseExceptionHandler("/Home/Error") re-executed
    // the pipeline against a Razor action that no longer exists, which would
    // have turned every unhandled exception into a 404 about the error page --
    // the failure mode where the thing reporting failures is the thing that
    // broke. The parameterless overload pairs with AddProblemDetails and writes
    // the body itself.
    app.UseExceptionHandler();

    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    //
    // This line did nothing at all until UseForwardedHeaders was added above
    // it, and that is the whole reason the line above exists. The default is
    // also `includeSubDomains` off and `preload` off, which is the right shape
    // here: the FQDN is a label under a suffix Azure shares between tenants,
    // and neither of those flags is something to hand to a domain this
    // application does not own.
    app.UseHsts();

    // Moved in here beside UseHsts, out of the pipeline it was in unconditionally.
    // The two answer the same reasoning and only one of them was being asked it:
    // both exist to stop real traffic travelling in the clear, and in development
    // there is no real traffic -- both ports are on loopback, and the certificate
    // is one this machine made for itself. UseHsts was already gated for exactly
    // that reason. The redirect was not, and the inconsistency was the bug.
    //
    // What it was costing: under the https profile, port 5150 answered every
    // request with a 307 to 7063. The Vite dev proxy does not follow redirects --
    // it hands the 307 to the browser, which then makes the cross-origin request
    // the proxy exists to prevent, against a self-signed certificate. What a
    // person sees is a CORS error naming neither the profile nor the redirect.
    // The workaround was to remember --launch-profile http every time, which
    // Visual Studio's run dropdown cannot be told to do; F5 picked https and the
    // client broke, for a reason nothing on the screen mentioned.
    //
    // This reverses half of what #4 settled -- the proxy still targets
    // http://localhost:5150, but the profile is now free. What lost, again: the
    // proxy pointing at https://localhost:7063 with secure: false, which works
    // and moves knowledge of the development certificate into the client's
    // config, and teaches a browser client to accept a certificate it did not
    // verify. That is a setting to keep out of a file that ships.
    //
    // The price is that development no longer exercises the redirect at all, so
    // a mistake in it would first appear in production. Small: behind Container
    // Apps the ingress terminates TLS and http never reaches this process, which
    // is the same reason this line does so little there.
    //
    // #36 settled what "so little" means, and it changed underneath this
    // comment. It used to be nothing-by-degradation: the middleware could not
    // find an https port and passed the request through, warning once. With
    // UseForwardedHeaders above, Request.IsHttps is true for anything arriving
    // through the ingress, so the middleware has nothing to redirect and the
    // warning is gone -- nothing-by-design, which is a different thing to read
    // in a log. It stays because the day this runs behind something that does
    // forward plain http, it is the line that catches it.
    app.UseHttpsRedirection();
}

app.UseRouting();

// The built React client, out of wwwroot, on the same origin as the API. One
// process, one image, one deployment -- and no CORS, because from the browser's
// point of view there is only ever one server.
//
// UseStaticFiles rather than MapStaticAssets, which is what stood here and is
// the .NET 10 default. MapStaticAssets resolves every file from a manifest
// written when the *.NET* project is compiled, and everything under wwwroot is
// produced by a different build system at a different moment. A file that is
// not in that manifest is a 404 in a published application -- verified, not
// assumed -- with nothing in the log to explain it, so getting `npm run build`
// and `dotnet publish` the wrong way round fails as a blank page. The
// framework's own warning names the alternative in as many words: "If the file
// was not added to the project during development, and is created at runtime,
// use the StaticFiles middleware to serve it instead."
//
// What that costs, exactly: MapStaticAssets generates .br and .gz beside every
// asset at publish time and negotiates them at request time. On this client's
// bundle that is 196,604 bytes down to 52,814. UseStaticFiles serves the file
// as it finds it. The 143 KB is worth paying attention to when slice 3 asks
// whether the URL works from a phone, and it is recoverable without touching
// this line -- ResponseCompression here, or the nginx container that CLAUDE.md
// already expects once the Python service makes it several containers anyway.
//
// The options are held in a variable because MapFallbackToFile below needs the
// same ones. It does not reuse this middleware -- it constructs its own -- so a
// policy set only here would apply to /index.html and not to "/", which is the
// URL people actually type.
var staticFileOptions = new StaticFileOptions
{
    // Vite writes content hashes into asset filenames precisely so they can be
    // cached forever: a changed file is a changed name, so there is nothing to
    // invalidate. index.html is the opposite -- its name never changes and it
    // is what names the current hashes, so caching it is how a browser pins
    // itself to a deployment that no longer exists.
    //
    // "immutable" is the part that earns this: without it a reload still sends
    // a conditional request per asset and spends a round trip being told 304.
    //
    // no-cache does not mean "do not store". It means "store it, but ask before
    // reusing it", which is exactly right for index.html: the answer is a 304
    // and no body whenever the deployment has not moved.
    //
    // Note what the test is actually keyed on, raised in review of #30: the
    // folder, not whether the filename carries a hash. Those coincide today
    // because Vite has two ways of emitting a file and only one lands here --
    // bundled output is hashed and written to /assets, while anything in
    // public/ is copied to the root untouched. That is why favicon.svg, which
    // keeps its name across deployments, correctly gets no-cache.
    //
    // The coincidence is what to remember. Drop a large image into public/
    // expecting asset caching and it will revalidate on every load, with the
    // reason three folders away in a build tool's conventions.
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl =
        file.Context.Request.Path.StartsWithSegments("/assets")
            ? "public, max-age=31536000, immutable"
            : "no-cache",
};

app.UseStaticFiles(staticFileOptions);

app.MapTransactionEndpoints();

// A wrong path under /api answers 404 as JSON, and this line is the only reason
// it does. MapFallbackToFile below matches "{*path:nonfile}", and `nonfile`
// asks whether the last segment looks like a filename -- not whether the
// request was meant for the API. /api/nope has no extension, so without this it
// matched the fallback and returned index.html with a 200: a client asking for
// a route that does not exist would be handed HTML, and whatever it does with
// that, it will not be reporting a 404.
//
// It sits after MapTransactionEndpoints but does not shadow it. Routing scores
// a literal segment above a catch-all parameter, so /api/transactions still
// reaches its own endpoint; this only collects what nothing else claimed.
//
// GET and HEAD, and deliberately not every method -- this exists only to
// counter the fallback, so it matches exactly what the fallback matches and
// nothing else. MapFallbackToFile answers `Allow: GET, HEAD`; no other method
// could ever have reached index.html, so no other method needs guarding here.
//
// Written as MapMethods after review of #30, where the unrestricted Map was
// found to be answering questions that routing answers better. An endpoint
// matching every method is a candidate for every request, and a surviving
// candidate is what stops routing from reporting *why* the real endpoint was
// rejected. Measured on the running app, before and after:
//
//   DELETE /api/transactions        404  ->  405, Allow: GET, HEAD, POST
//   POST   /api/transactions        404  ->  400, "Implicit body inferred for
//          with no Content-Type                   parameter \"request\" but no
//                                                 body was provided"
//
// The second is the worse of the two: a caller who forgot a header was being
// sent to hunt for a typo in their URL, when the API could tell them exactly
// what was missing. Restricting the methods leaves the real endpoint as the
// only candidate, and routing produces both answers by itself.
//
// HEAD is the one that does not come out clean, and it is in the list anyway.
// With it, `HEAD /api/transactions` is a 404 where 405 would be right -- no
// endpoint here declares HEAD, so this catch-all is what claims it. Without it,
// measured rather than assumed, both `HEAD /api/transactions` and
// `HEAD /api/nope` come back **200 text/html**: the fallback serves HEAD, so
// dropping HEAD reopens the whole hole for it. A 404 on a route that exists is
// a smaller lie than the index page on a route that does not.
//
// What would fix it properly is the list endpoint answering HEAD itself, at
// which point the literal route wins and this line never sees it. That belongs
// to #3's endpoints rather than to this one, and is left alone on purpose.
//
// A request under /api with any other method matches nothing at all and gets a
// bare 404 with no body -- honest, and not index.html.
//
// Results.Problem rather than Results.NotFound: the latter sends a bare status
// with no body, and the client's readProblem is looking for the RFC 9457 shape
// that AddProblemDetails writes everywhere else.
app.MapMethods(
    "/api/{**path}",
    [HttpMethods.Get, HttpMethods.Head],
    () => Results.Problem(statusCode: StatusCodes.Status404NotFound));

// What answers "/", and every client route under it.
//
// This replaces the redirect to the Vite dev server that stood here: with
// index.html in wwwroot, "/" is the client on every environment, and the
// Development-only branch that existed because this application had no page to
// give anyone is gone.
//
// The reason a fallback is needed at all is that a client that owns its routes
// will one day ask the server for /transactions/42 -- on a refresh, or on a
// link opened cold. There is no such file and no such endpoint, and without
// this line the honest answer is a 404 for a page the client can render
// perfectly well. Handing it index.html lets the client read the URL and route
// itself. There are no client routes yet -- App.tsx renders one screen -- so
// today this only serves "/", and it is here so that adding the first route is
// a client-side change and not a debugging session.
//
// Passing staticFileOptions is not decoration. MapFallbackToFile builds its own
// StaticFileMiddleware rather than reusing the one registered above, so without
// this argument "/" comes back with no Cache-Control at all while /index.html
// comes back with no-cache -- two answers for one file, and the one that is
// wrong is the one everybody requests.
//
// If index.html is missing -- a clone where `npm run build` has not run -- this
// answers 404 rather than pretending. That is the intended behaviour and it is
// also what F5 in Visual Studio now shows when only the API has been started:
// the fix is to build the client once, not to bring the redirect back.
app.MapFallbackToFile("index.html", staticFileOptions);

app.Run();
