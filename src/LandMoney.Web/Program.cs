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

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

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


app.Run();
