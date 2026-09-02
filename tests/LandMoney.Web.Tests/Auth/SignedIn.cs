using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LandMoney.Web.Tests.Auth;

/// <summary>A way past authorization that is not <c>UserManager</c>.</summary>
// Written for #67 and moved out of CategorySuggestionEndpointTests in #94, when a
// second class needed it. The reason a second copy would have been worse than a
// shared file is the usual one: what drifts between two copies of this is the
// PostConfigure below, and a copy that gets it wrong does not fail -- it silently
// authenticates with Identity's cookie scheme instead and every test in the file
// reports 401 for a reason that has nothing to do with what it is testing.
//
// It exists because a real sign-in needs SignInManager, which needs the user
// store, which needs Postgres -- and "the tests need no Postgres, no Docker and no
// network" is the property #22 was built on and CLAUDE.md defends. Nothing in the
// application can reach any of this: it is registered only through
// TestApp.With(...), so no production path grows a scheme that trusts everyone.
internal static class SignedIn
{
    // Named `Name` and not `Scheme`, which reads better and does not compile:
    // AuthenticationHandler already has a `Scheme` property, so the nested handler
    // below would resolve the constant to that instead and hand an
    // AuthenticationScheme where a string was wanted.
    public const string Name = "Test";

    /// <summary>The owner id every request made through this arrives as.</summary>
    // Named rather than a literal, so a test that wants to say "and the other
    // account sees nothing" has something to differ from. Nothing today does --
    // that check needs two accounts and a database, and is by hand.
    public const string OwnerId = "test-owner";

    public static void AddTo(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Handler>(
            Name, configureOptions: null);

        // PostConfigure rather than AddAuthentication(defaultScheme:), and the
        // difference decides whether any of this works. Identity's
        // AddIdentityCookies sets the default schemes through Configure, and so
        // would that overload -- so the two would race on registration order.
        // PostConfigure runs after every Configure there is, whoever registered it.
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = Name;
            options.DefaultChallengeScheme = Name;
            options.DefaultScheme = Name;
        });
    }

    /// <summary>The smallest handler that can succeed.</summary>
    private sealed class Handler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, OwnerId), new Claim(ClaimTypes.Name, "tester")],
                Name);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }
}
