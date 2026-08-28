using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace LandMoney.Web.Tests.Auth;

/// <summary>#52's "verified by", as far as it can be checked without a database.</summary>
// The issue asks for three things: a signed-out visitor gets no data, a signed-in
// one sees only their own rows, and another user's id returns nothing. The first
// is entirely here. The second and third belong to the query filter, and
// OwnershipFilterTests checks the SQL it produces.
//
// What is deliberately NOT here, said plainly because a suite that hides its gaps
// is worth less than a smaller one that does not: registering and signing in reach
// UserManager and SignInManager, which reach the database. Making those testable
// in process would mean a second EF provider whose behaviour is not Postgres's, or
// a Postgres container in CI -- and CLAUDE.md's "the tests need no Postgres, no
// Docker and no network" is a property #22 was built on. They are verified by hand
// against the compose database instead, and the run is written up in
// docs/deploy-azure.md. The invite-code rule, which is the part with a decision in
// it, is a pure function and is covered by RegistrationPolicyTests.
public class AuthorizationTests
{
    [Theory]
    [InlineData("/api/transactions")]
    [InlineData("/api/me")]
    public async Task An_anonymous_request_for_data_is_refused_with_401(string path)
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The half that matters as much as the status. Identity's cookie handler
    // redirects to /Account/Login by default -- a Razor page this application does
    // not have -- so without the events in AuthenticationSetup the client would
    // receive 404 HTML where it expected JSON and report a parse error about a
    // request that was actually refused.
    [Fact]
    public async Task An_anonymous_request_is_never_redirected()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/api/transactions");

        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task An_anonymous_post_is_refused_before_the_body_is_looked_at()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        // A body that would fail validation with a 400 if it ever reached the
        // filter. It must not: authorization runs first, and a 400 here would mean
        // an anonymous caller had learned something about the shape of the API.
        var response = await client.PostAsJsonAsync(
            "/api/transactions",
            new { occurredAt = "not-a-date", amount = -1, currency = "X", description = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // #62. The import endpoint is inside the group RequireAuthorization is applied
    // to, and this is the assertion that it stayed there -- a new MapPost written
    // outside the group would compile, work, and be public.
    //
    // The body is a file that would import cleanly if it ever reached the handler,
    // which is what makes the test worth having: a 200 here would mean an anonymous
    // caller had written rows into somebody's table.
    [Fact]
    public async Task An_anonymous_import_is_refused()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        using var content = new StringContent(
            "occurred_at,amount,currency,description\n2026-06-02,412.50,MDL,linella",
            Encoding.UTF8,
            "text/csv");

        var response = await client.PostAsync("/api/transactions/import", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The behaviour that changed when sign-in became a form. Under OpenID Connect
    // this endpoint required authorization and answered 302 to the provider; now it
    // has to serve the shell to a signed-out visitor, because the shell is what
    // draws the login form. Protecting it would mean refusing the request whose job
    // is to deliver the way back in.
    //
    // Asserted as "not refused" rather than "200", so that this stays a test about
    // authorization. A 404 here would mean the client had not been built into
    // wwwroot, which is a different fact and not this test's to report.
    [Fact]
    public async Task The_client_shell_is_served_to_a_signed_out_visitor()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
    }

    // Signing in cannot itself require being signed in -- that is a locked door
    // with the key inside. Checked with an empty body on purpose: ValidationFilter
    // refuses it with a 400 before the handler runs, so this proves the endpoint is
    // anonymous and reachable without going anywhere near UserManager or the
    // database.
    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/register")]
    public async Task Signing_in_does_not_itself_require_being_signed_in(string path)
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Signing out while already signed out is a no-op and must not be a 401 -- the
    // person whose cookie has expired is exactly the person trying to clear it.
    [Fact]
    public async Task Signing_out_when_signed_out_is_not_an_error()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // The process must start with no configuration at all: efbundle runs Program.cs
    // from a directory holding nothing but itself, and #57 is what a
    // required-configuration throw on that path costs. Building the host and
    // answering anything is the whole assertion; /api/nope is the catch-all of #20,
    // which is anonymous and touches nothing.
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task It_starts_with_no_invite_code_configured(string environment)
    {
        using var app = TestApp.WithoutInviteCode(environment);
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/api/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // And an unconfigured deployment still refuses data. "No invite code" must
    // never degrade into "no authentication" -- it closes registration and nothing
    // else.
    [Fact]
    public async Task An_unconfigured_deployment_still_refuses_data()
    {
        using var app = TestApp.WithoutInviteCode(Environments.Production);
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/api/transactions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
