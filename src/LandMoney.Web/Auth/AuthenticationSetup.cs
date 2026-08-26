using System.Security.Cryptography;
using System.Text;
using LandMoney.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace LandMoney.Web.Auth;

/// <summary>The whole of #52's wiring, so Program.cs keeps one line per feature.</summary>
// The same shape as MapTransactionEndpoints: an extension method, so the startup
// path names the feature instead of growing it.
public static class AuthenticationSetup
{
    /// <summary>The configuration key holding the code a new account must quote.</summary>
    public const string InviteCodeKey = "Authentication:InviteCode";

    /// <summary>
    /// Registers ASP.NET Core Identity with a cookie, and nothing else. There is no
    /// external provider and no redirect: the login form is part of the client.
    /// </summary>
    public static IServiceCollection AddLandMoneyAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger logger)
    {
        // ICurrentUser is registered whatever happens below, because AppDbContext
        // takes it and AppDbContext is constructed in places no request reaches --
        // `dotnet ef`, and the migration bundle.
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddSingleton(ReadRegistrationPolicy(configuration, environment, logger));

        // AddIdentityCore, not AddIdentity, and the difference is the reason this is
        // three calls instead of one. AddIdentity registers its own cookie schemes
        // AND sets the default challenge to a redirect at "/Account/Login" -- a
        // Razor page that does not exist here and would not be wanted if it did.
        // AddIdentityCore registers the managers and no schemes, so the cookies
        // below are the only ones configured and there is no path to a page nobody
        // wrote.
        services
            .AddIdentityCore<IdentityUser>(options =>
            {
                // Length over composition, which is the modern guidance and the
                // opposite of Identity's defaults (six characters, but one digit,
                // one upper, one lower and one symbol). Character-class rules push
                // people towards Password1! and buy less than four more characters
                // do.
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // Five attempts, then five minutes. On by default in Identity and
                // written out because PasswordSignInAsync has to opt into it per
                // call -- see AuthEndpoints -- so a reader who finds this block
                // could reasonably assume lockout is already happening when it is
                // not.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

                // No email is collected, so there is nothing to require to be
                // unique and nothing to confirm. This application has no flow that
                // would send a message: password reset was deliberately left out
                // (#52), because it means an email provider, an API key, a sender
                // domain and deliverability to debug. Storing a personal email
                // address for a flow that does not exist is worse than not storing
                // one.
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()

            // AddIdentityCore leaves this out, and without it SignInManager cannot
            // be resolved -- the failure is at first request rather than at
            // startup, and it names the type rather than the missing call.
            .AddSignInManager();

        services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "landmoney.auth";
            options.Cookie.HttpOnly = true;

            // Lax, and here it is doing more work than it was under OpenID Connect.
            // A Lax cookie is withheld from any cross-site request that is not a
            // top-level GET navigation, so a form on another site cannot POST to
            // /api/transactions with this user's session attached. That is the
            // CSRF protection for this application; there is no antiforgery token
            // anywhere, and this is why one is not needed. Changing this to None
            // removes it silently.
            options.Cookie.SameSite = SameSiteMode.Lax;

            // Always outside Development. The local loop is http://localhost:5150
            // (#4), where Always would mean the browser stores no cookie and the
            // sign-in appears to succeed and then not have happened. Deployed,
            // Request.IsHttps is true because of #36's UseForwardedHeaders.
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;

            // Every refusal is a status, never a redirect, and that is a change of
            // shape from the OpenID Connect version rather than a tweak. There, an
            // anonymous visitor was sent to a provider, so "/" had to be protected.
            // Here the login form IS the client, so the shell has to load for a
            // signed-out visitor in order to show it -- MapFallbackToFile is
            // anonymous, and the only protected things left are under /api, which
            // are called by `fetch` and want a status they can read.
            //
            // Without these two, the defaults redirect to /Account/Login and
            // /Account/AccessDenied. Neither exists, so the client would receive
            // 404 HTML where it expected JSON and report a parse error about a
            // request that was actually refused.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };

            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddAuthorization();

        return services;
    }

    private static RegistrationPolicy ReadRegistrationPolicy(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger logger)
    {
        var inviteCode = configuration[InviteCodeKey];

        if (!string.IsNullOrWhiteSpace(inviteCode))
        {
            return new RegistrationPolicy(inviteCode, RequiresInvite: true);
        }

        // The order of these two is the same trick the OpenID Connect version used
        // and it is kept for the same reason: the configured case is tested first,
        // so a wrong ASPNETCORE_ENVIRONMENT in Azure cannot open registration. The
        // deployed app has an invite code, so it takes the branch above whatever
        // the environment claims to be, and reaching the branch below would mean
        // deleting a secret rather than mistyping a word.
        if (environment.IsDevelopment())
        {
            logger.LogInformation(
                "No {Key} is configured, so registration on this machine needs no code. " +
                "This happens in the Development environment only.",
                InviteCodeKey);

            return new RegistrationPolicy(InviteCode: null, RequiresInvite: false);
        }

        // Fail closed, at the request rather than at startup. The process must
        // start: efbundle runs Program.cs from a directory with no configuration at
        // all, and #57 is what a required-configuration throw on that path costs.
        // Existing accounts keep working; only new ones are refused.
        logger.LogError(
            "No {Key} is configured and this is not the Development environment, so no new " +
            "account can be created. Anyone who already has one can still sign in. Set {Key}.",
            InviteCodeKey,
            InviteCodeKey);

        return new RegistrationPolicy(InviteCode: null, RequiresInvite: true);
    }
}

/// <summary>Whether a new account needs a code, and which one.</summary>
// A registered singleton rather than the endpoint reading configuration again:
// reading it twice is how two places come to disagree about which branch was
// taken. RequiresInvite is separate from InviteCode being null on purpose --
// "needs a code, and there is none configured" is the fail-closed state, and
// collapsing the two would make it indistinguishable from "needs no code".
public sealed record RegistrationPolicy(string? InviteCode, bool RequiresInvite)
{
    /// <summary>Whether this code opens the door.</summary>
    public bool Accepts(string? offered)
    {
        if (!RequiresInvite)
        {
            return true;
        }

        if (string.IsNullOrEmpty(InviteCode))
        {
            return false;
        }

        // Ordinal and fixed-time. A code is a shared secret, and comparing secrets
        // with == leaks their length and their matching prefix through how long the
        // comparison takes. The window here is small and the fix is one call, which
        // is a better trade than an argument about whether it is exploitable.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(InviteCode),
            Encoding.UTF8.GetBytes(offered ?? string.Empty));
    }
}
