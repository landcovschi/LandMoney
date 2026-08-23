using LandMoney.Web.Api;            // MapTransactionEndpoints
using LandMoney.Web.Data;          // AppDbContext
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
    // No path argument any more. UseExceptionHandler("/Home/Error") re-executed
    // the pipeline against a Razor action that no longer exists, which would
    // have turned every unhandled exception into a 404 about the error page --
    // the failure mode where the thing reporting failures is the thing that
    // broke. The parameterless overload pairs with AddProblemDetails and writes
    // the body itself.
    app.UseExceptionHandler();

    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
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
// Results.Problem rather than Results.NotFound: the latter sends a bare status
// with no body, and the client's readProblem is looking for the RFC 9457 shape
// that AddProblemDetails writes everywhere else.
app.Map("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound));

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
