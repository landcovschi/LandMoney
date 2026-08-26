using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LandMoney.Web.Auth;

/// <summary>The whole of #52's wiring, so Program.cs keeps one line per feature.</summary>
// The same shape as MapTransactionEndpoints: an extension method, so the startup
// path names the feature instead of growing it.
public static class AuthenticationSetup
{
    /// <summary>The cookie that carries the session once the provider has spoken.</summary>
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>The section every key below is read from.</summary>
    public const string ConfigurationSection = "Authentication:Oidc";

    /// <summary>The subject every row entered on a developer machine belongs to.</summary>
    // A fixed string rather than a fresh Guid per start: the rows in the local
    // Postgres have to still be visible after the application is restarted, or
    // the local database empties itself from the screen every time and the
    // ownership filter looks broken exactly when it is working.
    public const string DevelopmentSubjectId = "local-development-user";

    /// <summary>
    /// Registers authentication. OpenID Connect when it is configured; otherwise a
    /// local developer in Development, and nobody at all anywhere else.
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

        var section = configuration.GetSection(ConfigurationSection);
        var authority = section["Authority"];
        var clientId = section["ClientId"];
        var clientSecret = section["ClientSecret"];

        // Authority and ClientId decide it. ClientSecret is deliberately not part
        // of this test, because a public client using PKCE legitimately has none --
        // and treating a missing secret as "not configured" would drop such a
        // deployment into one of the two branches below without saying so.
        var oidcConfigured =
            !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(clientId);

        // Recorded once, here, where the branch is actually taken. AuthEndpoints
        // reads this rather than asking the configuration the same question a
        // second time.
        services.AddSingleton(new AuthenticationMode(oidcConfigured));

        // The order of these three branches is itself a decision, and it is the one
        // that keeps ASPNETCORE_ENVIRONMENT from being a door.
        //
        // #36 recorded that four middlewares hang off !IsDevelopment(), so a typo in
        // that variable turns four things off at once and says nothing.
        // Authentication would have been the fifth and by far the worst, except that
        // the configured case is tested FIRST: the deployed application has an
        // Authority, so it takes the OpenID Connect branch whatever the environment
        // claims to be. The development sign-in below is not reachable by setting
        // one environment variable -- it needs the provider configuration to be
        // absent as well, which in Azure means deleting a secret.
        if (oidcConfigured)
        {
            AddOpenIdConnect(services, environment, authority!, clientId!, clientSecret, section);
        }
        else if (environment.IsDevelopment())
        {
            // `dotnet run` with nothing configured still works, and still exercises
            // the ownership filter -- rows written here have an owner, it is just
            // always the same one. A local loop that ran unauthenticated would test
            // a code path production never takes.
            logger.LogInformation(
                "No {Section}:Authority is configured. Every request will be signed in as the " +
                "local development user. This happens in the Development environment only.",
                ConfigurationSection);

            services
                .AddAuthentication(LocalAuthenticationHandler.SchemeName)
                .AddScheme<LocalAuthenticationOptions, LocalAuthenticationHandler>(
                    LocalAuthenticationHandler.SchemeName,
                    options => options.SubjectId = DevelopmentSubjectId);
        }
        else
        {
            // Fail closed, at the request rather than at startup. The process has to
            // start -- efbundle runs Program.cs with no configuration at all -- and
            // it must not serve anything while it is in this state.
            logger.LogError(
                "No {Section}:Authority is configured and this is not the Development " +
                "environment, so nobody can sign in and every protected request will be " +
                "answered with 401. Set {Section}:Authority and {Section}:ClientId.",
                ConfigurationSection,
                ConfigurationSection,
                ConfigurationSection);

            services
                .AddAuthentication(LocalAuthenticationHandler.SchemeName)
                .AddScheme<LocalAuthenticationOptions, LocalAuthenticationHandler>(
                    LocalAuthenticationHandler.SchemeName,
                    options => options.SubjectId = null);
        }

        services.AddAuthorization();

        return services;
    }

    private static void AddOpenIdConnect(
        IServiceCollection services,
        IWebHostEnvironment environment,
        string authority,
        string clientId,
        string? clientSecret,
        IConfigurationSection section)
    {
        services
            .AddAuthentication(options =>
            {
                // The cookie holds the session; the provider is only asked when
                // there is no cookie. This is the "backend for frontend" shape, and
                // it is chosen over handing the SPA a token: the client is served by
                // this same application out of wwwroot, so a cookie is same-origin
                // and needs no CORS, no refresh logic in TypeScript, and puts no
                // access token anywhere JavaScript can read it.
                options.DefaultScheme = CookieScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "landmoney.auth";
                options.Cookie.HttpOnly = true;

                // Lax rather than Strict. The last leg of a sign-in is the provider
                // redirecting the browser back to /signin-oidc, which is a
                // cross-site navigation; Strict withholds the cookie on exactly that
                // request, and the symptom is a sign-in that loops for ever without
                // ever reporting an error.
                options.Cookie.SameSite = SameSiteMode.Lax;

                // SameAsRequest in Development only, and it is not a preference: the
                // local loop is http://localhost:5150 -- #4's decision, and the same
                // reason UseHttpsRedirection is gated this way -- so Always would
                // mean the browser never stores the cookie, and the sign-in would
                // appear to succeed and then not have happened. Deployed,
                // Request.IsHttps is true because of #36's UseForwardedHeaders, so
                // Always is a rule that holds rather than one that bites.
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;

                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;

                // OnRedirectToAccessDenied and not OnRedirectToLogin, and the
                // absence of the second one is the point.
                //
                // OnRedirectToLogin is the event every example of this pattern
                // uses, and here it would be dead code: it fires when the *cookie*
                // handler is challenged, and the challenge scheme above is
                // OpenIdConnect. An unauthenticated request is therefore handled by
                // the OpenID Connect handler, which redirects to the provider
                // without ever consulting this handler at all. The API-versus-page
                // split has to be made there instead, and it is --
                // OnRedirectToIdentityProvider, below.
                //
                // Worth knowing how that mistake presents, because it looks like
                // anything but a wrong event: `fetch` follows the 302 to the
                // provider, is answered with a sign-in page, and the client reports
                // a JSON parse error about a request that was actually refused for
                // a reason it never saw.
                //
                // This one is live: Forbid uses DefaultForbidScheme, which falls
                // back to DefaultScheme, which is the cookie. Nothing in this
                // application forbids anything yet -- there are no roles and no
                // policies beyond "signed in" -- so it cannot fire today. It is
                // here because the day a policy is added is not the day anyone
                // remembers that a 403 leaves this application as HTML.
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (IsApiRequest(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Authority = authority;
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;

                // The authorization code flow with PKCE. `code` rather than the
                // implicit `id_token`, which puts the token in a URL fragment and
                // therefore into browser history and into any proxy log that keeps
                // fragments. UsePkce defaults to true and is written out because it
                // is the part that makes a client with no secret safe.
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;

                // This application calls nothing on the user's behalf, so there is
                // no reason to keep the access token -- and keeping it puts it in
                // the cookie, which is sent on every single request to this origin.
                options.SaveTokens = false;

                options.Scope.Clear();
                foreach (var scope in ReadScopes(section))
                {
                    options.Scope.Add(scope);
                }

                // Some providers put `name` and `email` only at the userinfo
                // endpoint rather than in the id_token. One extra call per sign-in,
                // not per request, and without it the header can end up greeting a
                // subject id.
                options.GetClaimsFromUserInfoEndpoint = true;

                // MapInboundClaims is left at its default of true, so `sub` arrives
                // as ClaimTypes.NameIdentifier -- which is what CurrentUser reads
                // first. Turning it off is the tidier choice, and it would change
                // the claim every stored OwnerId was written from, so it is a
                // decision with a data migration attached rather than a style one.
                options.TokenValidationParameters.NameClaimType = "name";

                // Written out although both are the defaults, because they have to
                // be registered with the provider by hand and this is the only place
                // in the repository that says what they are.
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";

                // Where the provider is asked to send the browser after a sign-out
                // it has been told about. Relative, so it follows the host instead
                // of naming one.
                options.SignedOutRedirectUri = "/";

                // The line that keeps /api answering in JSON. Everything under that
                // prefix is called by `fetch` and never by a navigating browser, so
                // "you are not signed in" has to arrive as a status it can read
                // rather than as a redirect it will follow into HTML.
                //
                // HandleResponse is what stops the handler continuing to build the
                // authorization request after the status is set. Without it the
                // 401 is written, the redirect is then written over it, and the
                // symptom is that this event appears to do nothing.
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (IsApiRequest(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.HandleResponse();
                    }

                    return Task.CompletedTask;
                };
            });
    }

    private static IEnumerable<string> ReadScopes(IConfigurationSection section)
    {
        // A space-separated string rather than a configuration array, because that
        // is how a scope list is written everywhere else -- in the provider's own
        // console, in the specification, and in every error a provider returns
        // about one. Splitting it here costs a line and means the value can be
        // copied rather than transcribed.
        var configured = section["Scopes"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return ["openid", "profile", "email"];
        }

        return configured.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // StartsWithSegments, not StartsWith. "/apixyz" starts with the string "/api"
    // and is not in the API; the segment-aware overload is what knows the
    // difference, and it is the same distinction Program.cs's own /api catch-all
    // is already careful about.
    private static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api");
}
