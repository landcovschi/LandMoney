using System.Net;
using System.Net.Http.Json;
using LandMoney.Web.Auth;
using Microsoft.Extensions.Hosting;

namespace LandMoney.Web.Tests.Auth;

/// <summary>#52's "verified by", as far as it can be checked without a database.</summary>
// The issue asks for three things: a signed-out visitor gets a redirect and no
// data, a signed-in one sees only their own rows, and another user's id returns
// nothing. The first is entirely here. The second and third are properties of the
// query filter and need Postgres to observe end to end -- OwnershipFilterTests
// checks the SQL those two produce, and docs/deploy-azure.md records the run
// against a real database.
public class AuthorizationTests
{
    // ---------------------------------------------------------------------
    // A provider is configured. This is the deployed shape.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task An_anonymous_api_request_is_refused_with_401()
    {
        using var app = TestApp.WithProvider();
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/api/transactions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The half that matters more than the status, and the one a wrong event would
    // break silently. If the API redirected, `fetch` would follow it to the
    // provider, be answered with a sign-in page, and the client would report a
    // JSON parse error about a request that was refused for a reason it never saw.
    [Fact]
    public async Task An_anonymous_api_request_is_not_redirected_to_the_provider()
    {
        using var app = TestApp.WithProvider();
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/api/transactions");

        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task An_anonymous_post_is_refused_before_the_body_is_looked_at()
    {
        using var app = TestApp.WithProvider();
        using var client = app.CreateNonFollowingClient();

        // A body that would fail validation with a 400 if it ever reached the
        // filter. It must not: authorization runs first, and a 400 here would mean
        // an anonymous caller had learned something about the shape of the API.
        var response = await client.PostAsJsonAsync(
            "/api/transactions",
            new { occurredAt = "not-a-date", amount = -1, currency = "X", description = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_visitor_is_redirected_to_the_provider()
    {
        using var app = TestApp.WithProvider();
        using var client = app.CreateNonFollowingClient();

        // "/" is MapFallbackToFile, which is every client route. This is the
        // sentence "a signed-out visitor gets a redirect" in one assertion.
        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(
            TestApp.FakeAuthorizationEndpoint,
            response.Headers.Location?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_in_does_not_itself_require_being_signed_in()
    {
        using var app = TestApp.WithProvider();
        using var client = app.CreateNonFollowingClient();

        // AllowAnonymous on /auth/login, and the failure it prevents is a loop
        // rather than a refusal: an authorization requirement here would challenge,
        // and the challenge would come back here.
        var response = await client.GetAsync("/auth/login");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(
            TestApp.FakeAuthorizationEndpoint,
            response.Headers.Location?.ToString(),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // No provider configured, and not Development. The fail-closed branch.
    // ---------------------------------------------------------------------

    // The most important test in this file. "No identity provider is configured"
    // must never degrade into "no authentication is required" -- and it is exactly
    // the degradation a design that threw at startup would have avoided by never
    // starting, which #57 forbids. So it has to be checked at the request instead.
    [Theory]
    [InlineData("/api/transactions")]
    [InlineData("/api/me")]
    [InlineData("/")]
    public async Task An_unconfigured_deployment_refuses_everything(string path)
    {
        using var app = TestApp.WithoutProvider(Environments.Production);
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // And it still starts, which is the other half of the same decision: efbundle
    // runs Program.cs from a directory with no configuration at all, and a throw
    // for a missing Authority would kill the deploy at "Apply migrations" -- #57's
    // failure, one issue later. Building the host is the whole assertion.
    [Fact]
    public async Task An_unconfigured_deployment_still_starts()
    {
        using var app = TestApp.WithoutProvider(Environments.Production);
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/api/nope");

        // The /api catch-all of #20, which is AllowAnonymous and proves the
        // pipeline is assembled and answering rather than merely not throwing.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------------
    // No provider configured, in Development. The local loop.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Development_signs_every_request_in_as_the_local_developer()
    {
        using var app = TestApp.WithoutProvider(Environments.Development);
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/api/me");
        var me = await response.Content.ReadFromJsonAsync<Me>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AuthenticationSetup.DevelopmentSubjectId, me?.OwnerId);
    }

    // The one that would catch the mistake worth catching: a subject regenerated
    // per start makes every locally entered row invisible after a restart, and the
    // ownership filter then looks broken at exactly the moment it is working.
    [Fact]
    public async Task The_local_developer_is_the_same_person_after_a_restart()
    {
        string? first;
        string? second;

        using (var app = TestApp.WithoutProvider(Environments.Development))
        using (var client = app.CreateNonFollowingClient())
        {
            first = (await client.GetFromJsonAsync<Me>("/api/me"))?.OwnerId;
        }

        using (var app = TestApp.WithoutProvider(Environments.Development))
        using (var client = app.CreateNonFollowingClient())
        {
            second = (await client.GetFromJsonAsync<Me>("/api/me"))?.OwnerId;
        }

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    private sealed record Me(string? OwnerId, string? Name);
}
