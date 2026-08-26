using LandMoney.Web.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace LandMoney.Web.Tests.Auth;

/// <summary>The application, assembled in memory, with configuration this test chose.</summary>
// #21 refused Microsoft.AspNetCore.Mvc.Testing and was right to: an
// IEndpointFilter is an object with one method and needs no server. Authorization
// is the opposite -- whether an anonymous request is refused depends on the order
// of two middlewares and on metadata hung on an endpoint, neither of which is
// reachable from anything smaller than the assembled application.
//
// Nothing here needs Postgres, and that is a line worth defending rather than a
// happy accident: every request these tests make is refused before a handler runs,
// or is refused by ValidationFilter<T> before a handler runs. What that leaves
// out is said plainly in AuthorizationTests -- registering and signing in reach
// UserManager, which reaches the database, and those are verified by hand against
// the compose Postgres and written up in docs/deploy-azure.md.
internal sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string? _inviteCode;
    private readonly string _environment;

    private TestApp(string? inviteCode, string environment)
    {
        _inviteCode = inviteCode;
        _environment = environment;
    }

    /// <summary>Deployed and working: registration needs the code.</summary>
    public static TestApp WithInviteCode(string code = "let-me-in-please") =>
        new(code, Environments.Production);

    /// <summary>No code configured. Development opens registration; nothing else does.</summary>
    public static TestApp WithoutInviteCode(string environment) => new(null, environment);

    /// <summary>A client that reports a redirect instead of following it.</summary>
    // AllowAutoRedirect defaults to true, and with it on "this answered 302" is not
    // an assertion that can be made at all. Nothing here should redirect any more --
    // that is half of what these tests are checking -- so the client has to be able
    // to see one if it happens.
    public HttpClient CreateNonFollowingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);

        // UseSetting, not ConfigureAppConfiguration, and this is the trap of the
        // whole file. Program.cs reads its configuration while the
        // WebApplicationBuilder is still being assembled, and a callback registered
        // through ConfigureAppConfiguration is applied after that -- so the settings
        // arrive, correctly, too late to be seen, and every test fails with
        //
        //   System.InvalidOperationException : ConnectionStrings:Default is not set.
        //
        // which reads as a test that forgot to set it. UseSetting is early enough.
        //
        // Program.cs throws without a connection string by design: an application
        // that cannot reach its database has no job to do. Nothing here connects.
        builder.UseSetting(
            "ConnectionStrings:Default",
            "Host=test-only;Database=none;Username=none;Password=none");

        // Empty rather than absent, because appsettings.json ships a default and
        // this has to override it. An unconfigured categorizer has been a legal
        // state since #57: it warns and answers null. A base address here would be
        // a service every create test then waited two seconds for.
        builder.UseSetting("Categorizer:BaseUrl", string.Empty);

        builder.UseSetting(AuthenticationSetup.InviteCodeKey, _inviteCode ?? string.Empty);
    }
}
