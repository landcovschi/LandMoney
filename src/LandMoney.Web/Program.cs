using LandMoney.Web.Api;            // MapTransactionEndpoints
using LandMoney.Web.Data;          // AppDbContext
using Microsoft.EntityFrameworkCore; // UseNpgsql

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

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
    app.UseExceptionHandler("/Home/Error");
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

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// Mapped after the MVC route and it makes no difference: routing matches on the
// pattern, not on registration order, and /api/transactions cannot be confused
// with {controller}/{action}/{id?}. The MVC route and the Razor views under it
// are leftovers from the two Razor days and go away in #20, which is the issue
// that puts the built React client in wwwroot and therefore has to decide what
// answers "/". This said #6 until #6 was done and did not touch them: #6 is the
// React screen, served by Vite on its own port, and it never asks this app for
// a page at all.
app.MapTransactionEndpoints();


app.Run();
