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
}

app.UseHttpsRedirection();
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
// are leftovers from the two Razor days and go away in #6.
app.MapTransactionEndpoints();


app.Run();
