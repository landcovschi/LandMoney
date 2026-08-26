using System.Security.Claims;

namespace LandMoney.Web.Auth;

/// <summary>Who is making the request, reduced to the one fact rows are owned by.</summary>
// An interface rather than reaching for IHttpContextAccessor at every call site,
// and the reason is the same one that put TimeProvider behind a registration in
// #21: the alternative is an ambient static that tests cannot set. AppDbContext
// depends on this, and a DbContext that can only be constructed inside a live
// HTTP request is a DbContext no test can construct.
//
// One property, deliberately. A display name and an email are both available on
// the principal and neither belongs here -- this type exists so that the query
// filter has something to compare against, and anything else on it would be a
// second reason to inject it.
public interface ICurrentUser
{
    /// <summary>The signed-in subject, or null when nobody is signed in.</summary>
    string? OwnerId { get; }
}

/// <summary>Reads the subject out of the principal the authentication handler built.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? OwnerId
    {
        get
        {
            var user = accessor.HttpContext?.User;

            // IsAuthenticated, not a null check on the claim. An unauthenticated
            // request still carries a ClaimsPrincipal -- an empty one -- so
            // FindFirstValue on it answers null either way and the distinction
            // would be invisible. It is checked explicitly because the two
            // states mean different things one layer up: "signed out" is a 401,
            // "signed in with no subject" is a provider that is not sending the
            // claim, which is a configuration fault worth telling apart.
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            // Two spellings of one claim, and which one arrives depends on a
            // setting rather than on the provider. The OpenID Connect handler
            // maps `sub` onto ClaimTypes.NameIdentifier while MapInboundClaims is
            // true, which is its default and is left alone -- but the raw name is
            // what any other handler would use, and reading both costs a line.
            // The order matters only in that the mapped one is what production
            // actually produces.
            return user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue("sub");
        }
    }
}
