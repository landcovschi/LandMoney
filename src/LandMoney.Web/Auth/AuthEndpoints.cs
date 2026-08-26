using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace LandMoney.Web.Auth;

/// <summary>Whether an identity provider is configured, for the two endpoints that care.</summary>
// A registered singleton rather than the endpoints reading configuration again.
// Reading it twice is how two places end up disagreeing about which branch
// AddLandMoneyAuthentication took, and the disagreement would surface as
// "InvalidOperationException: No sign-out authentication handler is registered
// for the scheme 'OpenIdConnect'" -- a message about a scheme, for a cause in
// another file.
public sealed record AuthenticationMode(bool ProviderConfigured);

/// <summary>Sign in and sign out, as two links a browser can follow.</summary>
public static class AuthEndpoints
{
    /// <summary>Registers /auth/login and /auth/logout. Called from Program.cs.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth");

        // Not under /api, deliberately. These two are followed by a browser as
        // ordinary navigations -- they answer with redirects and set cookies --
        // and everything under /api is the opposite: JSON, no redirects, 401 rather
        // than a sign-in page. AuthenticationSetup.IsApiRequest draws that line by
        // path prefix, so putting these behind /api would make them answer 401
        // instead of starting the sign-in they exist to start.
        group.MapGet("/login", Login).AllowAnonymous();
        group.MapGet("/logout", Logout).AllowAnonymous();

        // Under /api, unlike the two above, because it is called by `fetch` and
        // wants a 401 rather than a redirect when the cookie has expired. That is
        // the whole distinction the prefix draws.
        //
        // It exists for the header: the client has to be able to say who is signed
        // in beside the sign-out link, and the subject id is not something to show
        // a person. It is also the only endpoint in the application that proves a
        // request was authenticated without reading the database, which is what
        // lets the tests assert the pipeline end to end with no Postgres.
        routes.MapGet("/api/me", Me).RequireAuthorization();

        return routes;
    }

    /// <summary>Who the caller is, for the sign-out line in the client's header.</summary>
    // The name is what a person recognises and the subject is what rows are owned
    // by, so both are here: a screen that says "signed in" without saying as whom
    // cannot be checked by the person reading it. Neither is a secret from its own
    // owner -- this endpoint answers about the caller and about nobody else, which
    // is why it takes no parameter to get wrong.
    private static IResult Me(ICurrentUser currentUser, HttpContext context) =>
        Results.Ok(new
        {
            OwnerId = currentUser.OwnerId,

            // Identity.Name is whatever NameClaimType points at -- "name" for the
            // OpenID Connect branch, ClaimTypes.Name for the local one. Falling
            // back to the subject means the header shows something rather than
            // nothing when a provider sends no profile claims.
            Name = context.User.Identity?.Name ?? currentUser.OwnerId,
        });

    private static IResult Login(
        HttpContext context,
        AuthenticationMode mode,
        string? returnUrl)
    {
        var destination = LocalOrRoot(returnUrl);

        if (!mode.ProviderConfigured)
        {
            // Development, where everyone is already signed in, or a deployment
            // with no provider configured, where nobody can be. Either way there is
            // no authorization endpoint to send the browser to, and a challenge
            // here would either loop or throw.
            return Results.Redirect(destination);
        }

        // Already signed in: sending the browser round the provider again would
        // work and would be a redirect nobody asked for.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return Results.Redirect(destination);
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = destination },
            [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    private static IResult Logout(AuthenticationMode mode)
    {
        if (!mode.ProviderConfigured)
        {
            // Said out loud rather than redirected to "/", which would look like a
            // sign-out that silently did nothing. There is no session to end: the
            // local scheme signs every request in from scratch and holds no cookie.
            return Results.Text(
                "There is no identity provider configured, so there is no session to end. " +
                $"See {AuthenticationSetup.ConfigurationSection} in appsettings.");
        }

        // Both schemes, and the order of the failure if only one is passed is worth
        // knowing: signing out of the cookie alone ends the session here while the
        // provider still holds one, so the next sign-in completes with no prompt
        // and reads as a sign-out that did not work. Signing out of the provider
        // alone leaves the cookie, which is worse -- the application would still
        // consider the user signed in.
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [AuthenticationSetup.CookieScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    }

    /// <summary>A return URL that cannot leave this site.</summary>
    // The open-redirect check, and it is the reason returnUrl is not simply passed
    // through. Without it, /auth/login?returnUrl=https://example.invalid is a link
    // on this origin that sends someone to another site *after* a successful
    // sign-in -- which is the shape that makes a phishing page convincing, because
    // the victim really did just sign in to the real application.
    //
    // Three things are refused, and the second and third are the ones that get
    // missed. "//example.invalid" is protocol-relative and is a different host.
    // "/\example.invalid" is the same trick with a backslash, which several
    // browsers normalise to a forward one.
    public static string LocalOrRoot(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)
            || returnUrl[0] != '/'
            || (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\')))
        {
            return "/";
        }

        return returnUrl;
    }
}
