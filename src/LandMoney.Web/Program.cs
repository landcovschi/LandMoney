using LandMoney.Web.Api;            // MapTransactionEndpoints, MapCategoryEndpoints
using LandMoney.Web.Auth;          // AddLandMoneyAuthentication, MapAuthEndpoints
using LandMoney.Web.Categorizing;  // CategorizerClient, CategorizerMetrics, CategorizerSummary
using LandMoney.Web.Data;          // AppDbContext
using System.Text.Json;            // JsonWriterOptions, for the JSON console formatter
using Microsoft.AspNetCore.HttpOverrides; // ForwardedHeaders
using Microsoft.EntityFrameworkCore; // UseNpgsql
using Microsoft.Extensions.Logging.Console; // JsonConsoleFormatterOptions

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

// What is deliberately NOT here: db.Database.Migrate(). It is the one line
// every tutorial puts under this one, so its absence is the surprising thing
// and needs the comment -- #37.
//
// Three reasons, and none of them is that it does not work:
//
// 1. --min-replicas 0. This app scales to zero and the first request after an
//    idle window pays 23.3 seconds of cold start already (measured, #35). A
//    migration on that path adds a schema change to the wake-up of a container
//    nobody is watching, triggered by whoever happened to open the URL.
//
// 2. Several replicas start at once when it scales up, and each would run
//    this. EF Core does take an ACCESS EXCLUSIVE lock on __EFMigrationsHistory,
//    so they serialise rather than corrupt each other -- verified in the bundle
//    output, which logs "Acquiring an exclusive lock for migration
//    application". What they do not do is start quickly: every replica after
//    the first waits for the first one's DDL before it can serve anything.
//
// 3. The failure shape is the worst of the three. A migration that throws in
//    here throws before app.Run(), so the container exits, Container Apps
//    restarts it, and it exits again. What that looks like from outside is an
//    application that will not start -- and the deployment that caused it
//    reported success. As a deployment step the same failure is a red step with
//    the SQL error in it, and the previous revision still serving.
//
// The schema arrives instead as `efbundle`, built by ci.yml from this commit
// and run against the database before the new revision is deployed. Step 13 of
// docs/deploy-azure.md is the whole of it.
//
// Note the consequence, since it is the cost rather than a benefit: the
// application will now happily start against a database whose schema is older
// than its model, and fail at the first query instead of at startup. Nothing
// checks this, deliberately -- a version check here would be the same startup
// coupling in a smaller coat.

// --- The Python categorizer -- #39 -------------------------------------------
//
// AddHttpClient registers CategorizerClient itself as well as its HttpClient, so
// the endpoint asks for the typed client and never sees an HttpClient. What the
// factory is really for is the handler underneath: it is pooled and rotated on a
// two-minute lifetime, which is the middle ground between a client per request
// (socket exhaustion) and one static client for the life of the process (a DNS
// answer cached for ever). Under compose the second is the live one -- recreating
// the categorizer container gives it a new address.
//
// BaseUrl comes from configuration and has a default in appsettings.json, so
// there is nothing to set for `dotnet run` on this machine: the default is
// http://localhost:8000, which is where docker-compose publishes the service. In
// compose the app container overrides it with Categorizer__BaseUrl=http://categorizer:8000
// -- the service name, because inside that network `localhost` is the app's own
// container and nothing is listening there. It is the first place in this project
// where the two words mean different things.
//
// Absent is a legal state, and this line is the whole of what broke the first
// deployment after #39. It used to be `?? throw`, matching the connection string
// twenty lines up -- and `efbundle` runs THIS FILE to find the DbContext, from a
// directory holding nothing but the bundle itself. appsettings.json is not
// beside it, so the key is missing, the throw fires, and the deploy job dies at
// "Apply migrations" with an error about a categorizer:
//
//   An error occurred while accessing the Microsoft.Extensions.Hosting services.
//   Error: Categorizer:BaseUrl is not set.
//   Unable to create a 'DbContext' of type 'AppDbContext'.
//
// CLAUDE.md had already recorded that trap for ConnectionStrings:Default in #37
// and it was not applied to the second key. The general form, which is the thing
// to remember rather than this instance: **every `?? throw` added to this file is
// also a deploy-time landmine**, because the bundle runs the host and sees only
// what the environment gives it. CI cannot catch it by building the bundle --
// that happens in the source tree, where appsettings.json exists. It is caught by
// *running* the bundle in an empty directory, which ci.yml now does on every
// pull request.
//
// So the two keys are treated differently on purpose, and the difference is not
// arbitrary. A missing connection string means the application cannot do its job
// and must not start. A missing categorizer means it does its job without a guess
// attached -- which is the entire design of #39, where every failure of that
// service becomes a null category rather than a failure. A dependency the
// application is built to run without must not be able to stop it starting.
//
// What that costs, and it is the reason this is not simply deleted: a mistyped
// *key* -- Categoriser:BaseUrl, say -- now degrades silently to no categorisation
// instead of failing loudly. The warning below is the only signal, which is why
// it is a warning and names the key. It is a small risk in practice, because
// appsettings.json ships inside the image and carries the default, so the key is
// present in every environment that serves a request.
var categorizerBaseUrl = builder.Configuration["Categorizer:BaseUrl"];

Uri? categorizerUri = null;

if (string.IsNullOrWhiteSpace(categorizerBaseUrl))
{
    // Not a throw and not silence. This is expected exactly once -- inside
    // efbundle, which never serves a request -- and anywhere else it means a
    // typo, so it has to be findable in a log.
    Console.WriteLine(
        "warn: Categorizer:BaseUrl is not set, so transactions will be stored with no category. "
        + "This is expected inside efbundle, which has no appsettings.json and never calls the "
        + "categorizer. Anywhere else it is a misconfiguration.");
}
// Present but unusable still throws, and that half is deliberately unchanged. A
// value someone typed and got wrong is a mistake to report, not a state to
// tolerate -- and it cannot break the bundle, because the bundle never has a
// value here at all.
else if (!Uri.TryCreate(categorizerBaseUrl, UriKind.Absolute, out categorizerUri))
{
    throw new InvalidOperationException(
        $"Categorizer:BaseUrl is '{categorizerBaseUrl}', which is not an absolute URI. "
        + "It needs a scheme: http://categorizer:8000, not categorizer:8000.");
}

// **Two numbers since #59, and this is the decision that issue actually turned
// on.** #39 gave the whole call two seconds, and the number was chosen against the
// *broken* case rather than the working one: `docker compose stop categorizer`
// leaves the SYN unanswered rather than refused, so while the service is down
// every save pays the full timeout on the path where the user's transaction is
// being written. A scan of 109 substrings needs milliseconds, so two seconds cost
// nothing when the service was up.
//
// A model call does not fit in two seconds, and the three routes #59 lists are all
// worse than they look. Keeping 2 s makes the deployed behaviour "rules or
// nothing" without saying so -- the failure that hides itself. Raising the single
// number re-prices the outage the 2 s was chosen for: eight seconds per save,
// every save, while the service is down. Categorising after the save is the
// architecturally honest answer, reverses #39's explicit "before SaveChangesAsync"
// decision, and needs somewhere to put follow-up work; it is its own issue.
//
// So the two failures are given two budgets, because they were only ever one
// number by accident:
//
//   ConnectTimeout   how long to wait for a service that is not there   2 s
//   Timeout          how long to wait for one that is thinking          8 s
//
// That keeps #39's measured property exactly -- a stopped categorizer still fails
// in two seconds, because that failure is a connection that never completes -- and
// spends the extra six only on a service that answered the SYN and is working.
// Both stay below the client's own REQUEST_TIMEOUT_MS of 10 s, so neither can be
// what makes the browser give up.
//
// What it does not cover, said out loud: a service that accepts the connection and
// then hangs costs the full eight seconds. That is the trade, and it is the right
// way round -- accepting a connection is evidence something is alive.
//
// Both are configuration rather than constants, so #60 can measure the model's
// real latency and move them without a code change.
builder.Services
    .AddHttpClient<CategorizerClient>(client =>
    {
        // Left null when nothing is configured, which is what CategorizerClient
        // reads to mean "there is no categorizer" -- it then answers null without
        // touching the network. Assigning a placeholder instead would make every
        // save pay the timeout below for a service that was never meant to exist.
        if (categorizerUri is not null)
        {
            client.BaseAddress = categorizerUri;
        }

        client.Timeout = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("Categorizer:TimeoutSeconds", 8d));
    })
    // ConfigurePrimaryHttpMessageHandler replaces the primary handler, and the
    // default already is a SocketsHttpHandler -- so this is the same handler with
    // one property set, not a different transport. The factory keeps rotating it on
    // its two-minute lifetime either way, which is the DNS behaviour the typed
    // client was registered for.
    //
    // ConnectTimeout applies to establishing a connection, so a pooled one skips it
    // entirely; it is paid on the first call and again after the service dies and
    // the pooled connection with it, which is exactly when it is wanted.
    //
    // **Its expiry surfaces as a cancellation, not as HttpRequestException**, which
    // is the opposite of what was written here first and was corrected by measuring
    // it: a save against an unreachable categorizer gave up after 2.15 s on
    // CategorizerClient's `OperationCanceledException` branch. So both clocks land
    // on the same catch, and that branch logs how long it actually waited rather
    // than naming one of the two limits and being wrong half the time.
    //
    // **The other measurement, and it cost the default in appsettings.json:** with
    // BaseUrl at `http://localhost:8000` this budget made things strictly worse. The
    // name resolves to `::1` first on Windows, nothing listens there, Docker Desktop
    // swallows the attempt rather than refusing it, and the dead attempt eats the
    // whole two seconds before the IPv4 fallback -- a save took the full eight
    // seconds and stored no category, against 156 ms once the key held an address.
    // The fix is the address, not a larger number here.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("Categorizer:ConnectTimeoutSeconds", 2d)),
    });

// --- What the categorizer is actually doing -- #64 ---------------------------
//
// AddMetrics is what registers IMeterFactory, and it is written out although the
// web host already calls it: this file is also run by efbundle and by anything
// else that builds the host, and a singleton whose dependency is registered by
// somebody else's default is a startup failure waiting for a framework change.
// The call is TryAdd underneath, so saying it twice costs nothing.
builder.Services.AddMetrics();

// A singleton, because a counter that resets per request counts nothing. It is
// resolved by CategorizerClient, which is registered as a transient typed client
// above -- the usual captive-dependency rule runs the other way and is satisfied:
// a shorter-lived object may hold a longer-lived one.
builder.Services.AddSingleton<CategorizerMetrics>();

// The thing that makes the counts readable without a metrics endpoint, which #64
// defers to its own issue. It writes one line per window in which anything
// happened, and nothing at all otherwise.
builder.Services.AddHostedService<CategorizerSummary>();

// **JSON on the console outside Development, and this is the half of #64 that is
// not about the categorizer at all.** The default console formatter writes two
// lines per entry -- a header, then the rendered sentence, indented -- and throws
// the structured fields away in the rendering. Container Apps forwards stdout to
// Log Analytics a line at a time, so today one log entry arrives as two rows,
// neither of which carries `Outcome` as anything a query could group by. Naming
// the outcomes consistently would then buy nothing: the names would be inside
// prose.
//
// With the JSON formatter each entry is one line, and every placeholder in a
// message template is a field beside the message. "How often was it unreachable
// last week" becomes a query over `Outcome` rather than a substring search over a
// sentence somebody may reword -- which is what #64 means by readable without
// grepping, and it is the property that survives the process dying every quarter
// of an hour with its counters inside it.
//
// Indented = false is not cosmetic: multi-line JSON would be several rows again,
// which is the exact failure being fixed.
//
// Development keeps the human formatter. There the reader is a person watching a
// terminal, the log is not being queried, and JSON one line at a time is worse to
// read than the sentence it replaces.
//
// What this does not do, deliberately: add a logging package. AddJsonConsole is
// in the framework, it configures the console provider that is already there
// rather than adding a second one, and nothing here depends on Serilog or on an
// OpenTelemetry exporter. Those become worth discussing when there is somewhere
// to export to.
//
// One function rather than two copies, because there are two places here that
// configure a console -- this one and the startup factory further down -- and a
// timestamp format written twice is a format that ends up written two ways.
static void AsJson(JsonConsoleFormatterOptions options)
{
    // Indented = false is not cosmetic: multi-line JSON is several rows again,
    // which is the exact failure being fixed.
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };

    // Container Apps stamps its own timestamp on each row, so this is a duplicate
    // -- and it is the only one of the two that says which clock it came from, and
    // it stays unambiguous when a line is copied out of a query into an issue. UTC,
    // per CLAUDE.md; ISO 8601, so it sorts as text.
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.UseUtcTimestamp = true;
}

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole(AsJson);
}

// #52. Everything about which provider, and what happens when there is none, is
// in AuthenticationSetup -- including the reason this cannot be a `?? throw` for
// a missing Authority the way the connection string above is. The short version:
// efbundle runs this file with no configuration at all, and #57 is what a throw
// on that path costs.
//
// A logger is passed in because this runs before builder.Build(), so there is no
// ILogger<T> to resolve yet. CreateBootstrapLogger-style plumbing would be the
// tidy answer and is a dependency; a factory built here, used once and disposed
// is four lines.
// The formatter has to be chosen here as well, and finding that out took reading
// the Production log rather than the code: this factory is built by hand, so it
// knows nothing about the AddJsonConsole above, which configures the *host's*
// logging. Without this line the two startup lines from AuthenticationSetup --
// including the error saying registration is closed, which is exactly the kind of
// line a query wants -- arrive in the deployed log as the two-line human format
// while everything after them is JSON.
//
// One line above this is still neither: the `Console.WriteLine` for a missing
// Categorizer:BaseUrl. It is deliberate and it stays -- it exists for efbundle,
// which runs this file with no logging pipeline configured at all.
using (var startupLoggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

    if (builder.Environment.IsDevelopment())
    {
        logging.AddConsole();
    }
    else
    {
        logging.AddJsonConsole(AsJson);
    }
}))
{
    builder.Services.AddLandMoneyAuthentication(
        builder.Configuration,
        builder.Environment,
        startupLoggerFactory.CreateLogger("LandMoney.Web.Auth"));
}

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

// #52. After UseRouting, because both of these want to know which endpoint was
// matched -- UseAuthorization has to read the endpoint's metadata to find the
// RequireAuthorization put there below, and with the order reversed it finds
// nothing and allows everything. This is the one middleware ordering mistake in
// ASP.NET Core that fails open rather than throwing.
//
// Their position relative to UseStaticFiles has no effect, which is worth saying
// out loud: the static file middleware is not an endpoint and consults no
// authorization metadata, so wwwroot is served to anyone wherever these two sit.
// The client is public on purpose -- it is the same bytes for every visitor, and
// one of the screens it can draw is the login form. Everything behind it is under
// /api and refuses an anonymous request.
app.UseAuthentication();
app.UseAuthorization();

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

app.MapTransactionEndpoints().RequireAuthorization();

// #63. The eleven categories the correction dropdown is built from. Its own
// RequireAuthorization, because a group only inherits what is applied to it --
// there is no ambient rule here, so a new group added without this line is public.
app.MapCategoryEndpoints().RequireAuthorization();

// #52. Register, sign in, sign out, and /api/me.
app.MapAuthEndpoints();

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
// Anonymous, deliberately, and this is where the shape of #52 changed when the
// sign-in became a form instead of a redirect to a provider.
//
// The first version of #52 required authorization here: an anonymous visitor was
// sent to an identity provider, so the shell never had to render for one. With
// the login form living inside the client, the shell is exactly what a signed-out
// visitor needs -- protecting it would mean answering 401 to the request whose
// job is to deliver the form.
//
// Nothing is given away by that. index.html and the bundle are the same bytes for
// every visitor and hold no data; the only things behind them are under /api, and
// those refuse an anonymous request. It also makes the pipeline honest about what
// was already true: UseStaticFiles is not an endpoint and consults no
// authorization metadata, so /index.html was being served to anyone regardless.
app.MapFallbackToFile("index.html", staticFileOptions);

app.Run();

// #52. Top-level statements compile into an `internal` class called Program, and
// WebApplicationFactory<T> needs T to be reachable from the test assembly. This
// partial declaration makes the generated class public without giving it a body
// or a second Main.
//
// The alternative is [assembly: InternalsVisibleTo("LandMoney.Web.Tests")], which
// keeps the type internal and opens every other internal in this assembly to the
// tests -- a wider hole to close a narrower gap. This line is the documented one.
//
// #21 refused Microsoft.AspNetCore.Mvc.Testing on the grounds that an
// IEndpointFilter is an object with one method and needs no server. Authorization
// is not: whether a filter hangs on the POST or on the group, whether the pipeline
// order is right, and what status an anonymous request actually receives are
// properties of the assembled application and of nothing smaller. #52 named this
// as the day that package earns its place, and this is it.
public partial class Program;
