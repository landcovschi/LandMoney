using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LandMoney.Web.Tests.Auth;

/// <summary>The application, assembled in memory, with configuration this test chose.</summary>
// #21 refused Microsoft.AspNetCore.Mvc.Testing and was right to: an
// IEndpointFilter is an object with one method and needs no server. Authorization
// is the opposite -- whether an anonymous request is refused depends on the order
// of two middlewares, on metadata attached to an endpoint, and on which of three
// branches AddLandMoneyAuthentication took. None of that is reachable from
// anything smaller than the assembled application, and #52 named this as the day
// the package earns its place.
//
// Nothing here needs Postgres, and that is deliberate rather than lucky: every
// request these tests make is refused before a handler runs, or is answered by
// /api/me, which reads the principal and nothing else. CLAUDE.md's "the tests
// need no Postgres, no Docker and no network" survives #52 intact.
internal sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly string _environment;

    private TestApp(Dictionary<string, string?> settings, string environment)
    {
        _settings = settings;
        _environment = environment;
    }

    /// <summary>Deployed and working: an identity provider is configured.</summary>
    public static TestApp WithProvider(string? environment = null) =>
        new(
            new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = FakeAuthority,
                ["Authentication:Oidc:ClientId"] = "landmoney-tests",
                ["Authentication:Oidc:ClientSecret"] = "not-a-real-secret",
            },
            environment ?? Environments.Production);

    /// <summary>Nothing configured. In Production this is the fail-closed branch.</summary>
    public static TestApp WithoutProvider(string environment) =>
        new([], environment);

    /// <summary>An authority that is never contacted. See ConfigureWebHost below.</summary>
    public const string FakeAuthority = "https://provider.invalid";

    /// <summary>Where a challenged browser is expected to be sent.</summary>
    public const string FakeAuthorizationEndpoint = FakeAuthority + "/authorize";

    /// <summary>A client that reports a redirect instead of following it.</summary>
    // AllowAutoRedirect defaults to true, and with it on the assertion "this
    // answered 302" cannot be made at all: HttpClient follows the Location to
    // provider.invalid, fails to resolve it, and the test reports a DNS error.
    public HttpClient CreateNonFollowingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);

        // UseSetting, not ConfigureAppConfiguration, and this is the trap of the
        // whole file. Program.cs reads its configuration while the
        // WebApplicationBuilder is still being assembled -- GetConnectionString on
        // line 62, Categorizer:BaseUrl a hundred lines later -- and a callback
        // registered through ConfigureAppConfiguration is applied after that. So
        // the settings arrive, correctly, too late to be seen, and every test in
        // this file fails with:
        //
        //   System.InvalidOperationException : ConnectionStrings:Default is not set.
        //
        // which reads as a test that forgot to set it. UseSetting writes into the
        // host configuration the builder starts from, which is early enough.
        //
        // Program.cs throws without a connection string by design -- an application
        // that cannot reach its database has no job to do. Nothing here connects:
        // no test in this file reaches a handler that queries.
        builder.UseSetting(
            "ConnectionStrings:Default",
            "Host=test-only;Database=none;Username=none;Password=none");

        // Empty rather than absent, because appsettings.json ships a default and
        // this has to override it. An unconfigured categorizer has been a legal
        // state since #57: it logs a warning and answers null. A base address here
        // would be a service every create test then waited two seconds for.
        builder.UseSetting("Categorizer:BaseUrl", string.Empty);

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            // The one thing that would otherwise touch the network, and it took
            // two attempts to write correctly.
            //
            // Without it the handler fetches
            // https://provider.invalid/.well-known/openid-configuration the first
            // time it is challenged, and every test here answers 500 with
            // `IDX20803: Unable to obtain configuration from` -- which reads as a
            // broken provider rather than as a test that reached the internet.
            //
            // Setting options.Configuration is the obvious fix and does nothing.
            // The framework's own post-configure has already built a
            // ConfigurationManager from Authority, and the handler prefers the
            // manager whenever it has one; a later PostConfigure assigning
            // Configuration loses silently. StaticConfigurationManager is the type
            // that means "here is the answer, never go and ask" -- replacing the
            // manager rather than the value it would have fetched.
            services.PostConfigure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.ConfigurationManager =
                    new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = FakeAuthority,
                            AuthorizationEndpoint = FakeAuthorizationEndpoint,
                            TokenEndpoint = FakeAuthority + "/token",
                            EndSessionEndpoint = FakeAuthority + "/logout",
                        }));
        });
    }
}
