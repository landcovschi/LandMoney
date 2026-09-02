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
    // /api/categories is #63's, and it is in this list for a reason that is not
    // about the eleven words being secret -- they are in a public repository and in
    // docs/evals.md. It is that it is a group of its own, so it inherits nothing:
    // MapCategoryEndpoints without a RequireAuthorization beside it in Program.cs
    // compiles, works, and is public. This is the line that reports that.
    //
    // #95 added the last two. The summary is a `GROUP BY` over one person's spending
    // and reads as an aggregate, which is exactly the shape somebody argues is not
    // really data; it is one account's month, and it is refused. The count is a
    // single integer and is refused for the sharper reason: the POST beside it
    // spends money, so a number that says how much would be the first half of
    // deciding whether it is worth reaching for.
    [Theory]
    [InlineData("/api/transactions")]
    [InlineData("/api/transactions?limit=5")]
    [InlineData("/api/transactions/summary?month=2026-08")]
    [InlineData("/api/transactions/backfill-categories")]
    [InlineData("/api/categories")]
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

    // #89. The export endpoint is inside the group RequireAuthorization is applied
    // to, and this is the assertion that it stayed there. It is the one route in this
    // application whose whole answer is somebody's data in a file -- the list endpoint
    // returns the same rows, but an export is the shape that gets saved, mailed and
    // committed -- so a MapGet written outside the group would be one URL between an
    // account's spending and anyone who guessed the path.
    //
    // Asserted with 401 rather than "not 200", because the query filter would answer
    // an anonymous caller with an empty file: owner_id compared to NULL is never true,
    // which is AppDbContext's deliberate fail-closed behaviour. A header-only CSV and
    // a refusal look alike from a distance and are not the same fact, and it is the
    // authorization that is being checked here.
    [Fact]
    public async Task An_anonymous_export_is_refused()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.GetAsync("/api/transactions/labelled");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // #93. The backfill endpoint is inside the group RequireAuthorization is applied
    // to, and this is the assertion that it stayed there. It is the one route in this
    // application that spends money -- every row it marks is a model call the sweep
    // will go and make -- so a POST written outside the group would be an unmetered
    // way for anyone who guessed the path to run up somebody else's Anthropic bill,
    // which is the same failure #61 kept the categorizer's ingress internal to
    // prevent.
    //
    // Asserted with 401 rather than "not 200" for the reason the export is: the query
    // filter would answer an anonymous caller by marking nothing, and "0 marked" and
    // "refused" look alike from a distance and are not the same fact.
    [Fact]
    public async Task An_anonymous_backfill_is_refused()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.PostAsync("/api/transactions/backfill-categories", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // #63. The correction endpoint is inside the group RequireAuthorization is
    // applied to, and this is the assertion that it stayed there.
    //
    // The id is one that cannot exist, and the body is one that would be accepted
    // if it reached the handler -- so a 404 here would be as bad as a 200: it would
    // mean an anonymous caller had been told whether somebody else's transaction
    // exists. Authorization has to answer before the row is looked for.
    [Fact]
    public async Task An_anonymous_correction_is_refused()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.PatchAsJsonAsync(
            $"/api/transactions/{Guid.NewGuid()}",
            new { category = "groceries" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // #94. The correction endpoint is inside the group RequireAuthorization is
    // applied to, and this is the assertion that it stayed there. The body is one
    // that would be stored if it reached the handler, so a 200 here would mean an
    // anonymous caller had rewritten somebody's spending.
    //
    // The id is one that cannot exist, and a 404 would be as bad as a 200 for the
    // reason the category correction's test records: it would tell an anonymous
    // caller whether somebody else's transaction exists.
    [Fact]
    public async Task An_anonymous_correction_of_a_whole_transaction_is_refused()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.PutAsJsonAsync(
            $"/api/transactions/{Guid.NewGuid()}",
            new
            {
                occurredAt = "2026-09-01",
                amount = 42.50m,
                currency = "EUR",
                description = "linella",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // #94. The delete endpoint is inside the group RequireAuthorization is applied
    // to, and this is the only route in this application that destroys data. There
    // is no undo -- the row is gone from Postgres, deliberately, because a soft
    // delete would keep blocking the import's duplicate detection -- so a DELETE
    // written outside the group would be one guessed URL between somebody's year of
    // history and nothing.
    [Fact]
    public async Task An_anonymous_delete_is_refused()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.DeleteAsync($"/api/transactions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // #67. The suggestion endpoint is inside the group RequireAuthorization is
    // applied to, and this is the assertion that it stayed there. It matters more
    // than the shape of the endpoint suggests: it is the one route in this
    // application that spends money on behalf of whoever calls it, since against a
    // model every request is a charge, and it writes nothing -- so an unauthorized
    // one would leave no trace anywhere except a bill.
    //
    // The body is one that would be answered if it reached the handler, which is
    // what makes a 200 here as bad as a 500.
    [Fact]
    public async Task An_anonymous_category_suggestion_is_refused()
    {
        using var app = TestApp.WithInviteCode();
        using var client = app.CreateNonFollowingClient();

        var response = await client.PostAsJsonAsync(
            "/api/transactions/category-suggestion",
            new { amount = 42.50m, currency = "EUR", description = "Lidl" });

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
