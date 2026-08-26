using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LandMoney.Web.Auth;

/// <summary>Options for the scheme used when no identity provider is configured.</summary>
public sealed class LocalAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The subject to sign every request in as, or null to sign nobody in.</summary>
    // Null is not "unset by mistake", it is the second of the two modes this
    // handler exists for. See the class below.
    public string? SubjectId { get; set; }

    /// <summary>What to call that subject on screen.</summary>
    public string DisplayName { get; set; } = "Local developer";
}

/// <summary>
/// The authentication scheme that runs when Authentication:Oidc is not configured.
/// Signs everyone in as one fixed local user, or signs nobody in at all.
/// </summary>
// Two modes in one handler because they are the same question asked in two
// environments, and both answers have to exist for the same reason: this
// application must start with no identity provider configured.
//
// That requirement is not a preference. `efbundle` runs Program.cs to find the
// DbContext, from a directory holding nothing but itself, in Production, with no
// appsettings.json -- which is how #57's `?? throw` for Categorizer:BaseUrl
// killed a deployment at "Apply migrations". CLAUDE.md records the general rule
// it earned: every `?? throw` in Program.cs is also a deploy-time landmine. A
// throw for a missing Authority would be exactly that landmine again, and this
// time it would be in the half of the pipeline that runs before any revision is
// replaced -- so the schema would stop moving and the reason would read as a
// broken migration.
//
// So the process starts either way, and the fail-closed part is moved from
// startup to the request:
//
//   SubjectId set   (Development, nothing configured) -> everyone is the local
//                                                        developer
//   SubjectId null  (anywhere else, nothing configured) -> nobody is anybody,
//                                                          and every request
//                                                          protected by
//                                                          RequireAuthorization
//                                                          is a 401
//
// The second mode is the one worth being careful about, because "no provider
// configured" must never degrade into "no authentication required". It does not:
// NoResult() means the request is anonymous, and an anonymous request to an
// endpoint with RequireAuthorization is refused. The application starts, serves
// nothing, and says why in the log. That is the shape #57 argued for -- a
// dependency must not be able to stop the process starting -- with the half it
// did not need added: it must not be able to open the door either.
internal sealed class LocalAuthenticationHandler(
    IOptionsMonitor<LocalAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<LocalAuthenticationOptions>(options, logger, encoder)
{
    /// <summary>The scheme name, referenced from Program.cs and from the tests.</summary>
    public const string SchemeName = "Local";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Options.SubjectId is null)
        {
            // NoResult, not Fail. Fail is for a credential that was presented and
            // was wrong, and it surfaces as an error; there is no credential here
            // and nothing went wrong with the request. NoResult is "this scheme
            // has nothing to say", which is what leaves the request anonymous.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Options.SubjectId),
                new Claim(ClaimTypes.Name, Options.DisplayName),
            ],

            // The authentication type has to be non-null or ClaimsIdentity reports
            // IsAuthenticated false with the claims sitting right there, which is
            // a very quiet way to fail.
            SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    // 401 rather than the base class's behaviour, and never a redirect: there is
    // nowhere to redirect to. This scheme is only ever the challenge scheme when
    // no provider is configured, and sending a browser to a sign-in page that
    // does not exist would turn a configuration fault into a loop.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
