using System.Security.Claims;
using LandMoney.Web.Auth;
using Microsoft.AspNetCore.Http;

namespace LandMoney.Web.Tests.Auth;

/// <summary>Which claim a row's owner is read from, and when there is no owner.</summary>
public class CurrentUserTests
{
    [Fact]
    public void The_subject_comes_from_the_mapped_claim()
    {
        // What production actually produces: MapInboundClaims is left at its
        // default, so the OpenID Connect handler rewrites `sub` to this before the
        // principal is ever seen here.
        var currentUser = For(new Claim(ClaimTypes.NameIdentifier, "subject-from-provider"));

        Assert.Equal("subject-from-provider", currentUser.OwnerId);
    }

    [Fact]
    public void The_unmapped_claim_is_read_when_it_is_the_only_one()
    {
        var currentUser = For(new Claim("sub", "subject-from-provider"));

        Assert.Equal("subject-from-provider", currentUser.OwnerId);
    }

    [Fact]
    public void There_is_no_owner_when_nobody_is_signed_in()
    {
        // An unauthenticated request still carries a ClaimsPrincipal -- an empty
        // one -- so this is a real state rather than a null-reference waiting to
        // happen, and it is the state the query filter turns into "no rows".
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() },
        };

        Assert.Null(new CurrentUser(accessor).OwnerId);
    }

    // The mutation this kills: dropping the IsAuthenticated check, on the grounds
    // that FindFirstValue answers null anyway. It usually does -- and a principal
    // carrying claims from an identity whose AuthenticationType is null reports
    // IsAuthenticated false while the claim sits right there, which is how an
    // unauthenticated request would come to own rows.
    [Fact]
    public void Claims_on_an_unauthenticated_identity_do_not_count()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "smuggled")]);
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        Assert.False(identity.IsAuthenticated);
        Assert.Null(new CurrentUser(accessor).OwnerId);
    }

    // AppDbContext is constructed by `dotnet ef` and by the migration bundle, where
    // there is no request at all. It resolves ICurrentUser, so this path is walked
    // on every deploy -- and a NullReferenceException here would surface as
    // "Unable to create a 'DbContext'", which reads as a broken migration.
    [Fact]
    public void There_is_no_owner_outside_a_request()
    {
        Assert.Null(new CurrentUser(new HttpContextAccessor()).OwnerId);
    }

    private static CurrentUser For(Claim claim)
    {
        var identity = new ClaimsIdentity([claim], authenticationType: "Test");
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        return new CurrentUser(accessor);
    }
}
