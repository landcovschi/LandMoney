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

// Serves whatever is in wwwroot, which is nothing at all today -- the folder was
// emptied with the Razor pages that referenced it, jQuery and Bootstrap
// included. It stays because #20 fills that folder with the built React client,
// and this is the line that will serve it.
app.MapStaticAssets();

app.MapTransactionEndpoints();

// What answers "/" in development, and only in development.
//
// The client is served by Vite on its own port while developing, so this
// application has no page to give anyone -- it is an API. Before this it
// answered with the Razor template's "Welcome" page, which was worse than
// nothing: pressing F5 and being shown a stranger's landing page suggests the
// wrong thing is running.
//
// A redirect rather than a 404, because the browser Visual Studio opens on F5
// lands here, and sending it where the application actually is costs one line.
// It is deliberately not a general fallback -- only the root -- so a wrong URL
// still 404s honestly instead of being bounced to a dev server that may not be
// up.
//
// #20 replaces this with index.html out of wwwroot, at which point "/" is the
// client on every environment and this branch goes away. The port is Vite's
// default and is written down in src/landmoney.client/README.md; if it ever
// moves, both change together or F5 lands on nothing.
if (app.Environment.IsDevelopment())
{
    app.MapGet("/", () => Results.Redirect("http://localhost:5173"));
}

app.Run();
