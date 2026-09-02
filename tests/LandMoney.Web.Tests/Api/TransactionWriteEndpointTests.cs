using System.Net;
using System.Net.Http.Json;
using LandMoney.Web.Tests.Auth;

namespace LandMoney.Web.Tests.Api;

/// <summary>#94's two new routes, as far as a suite with no database can follow them.</summary>
// **Where the wall is, said before the tests rather than after them.** Both
// handlers reach AppDbContext, so nothing here can assert what they store, what
// they delete, or that another account gets a 404 -- that is the same wall
// AuthorizationTests and #62 both document, and those checks were made by hand
// against the compose stack instead.
//
// What is on this side of it is worth having and is exactly the half that fails
// silently. A request refused before the handler runs never touches a connection:
// routing decides whether the URL matched, and ValidationFilter<T> decides whether
// the body is allowed. So these tests answer two questions that reading the diff
// cannot -- is the filter attached to the PUT at all, and are the rules it runs
// CreateTransactionRequest's. #94's fourth trap is precisely that the second one
// stops being true without anything reporting it, and UpdateTransactionRequestTests
// proves it about the type where this proves it about the route.
//
// The authentication is stubbed, for the reason Auth/SignedIn.cs gives: a real
// sign-in is UserManager, which is Postgres. AuthorizationTests holds the other
// half -- that an anonymous caller reaches neither of these.
public class TransactionWriteEndpointTests
{
    private static TestApp SignedInApp() => TestApp.With(SignedIn.AddTo);

    private static string Path(object id) => $"/api/transactions/{id}";

    // --- what the route matches ----------------------------------------------

    // The {id:guid} constraint, and it is doing more than tidying. #63 chose it so
    // the PATCH could not swallow /import; the PUT and the DELETE inherit both that
    // and its second effect, which is the one being asserted -- a malformed id is a
    // 404 from routing rather than a 400 from the binder. That is the right answer
    // rather than a convenient one: an id that cannot exist and an id that does not
    // exist are the same fact to a caller, and answering them differently is how a
    // caller learns which ids are real.
    //
    // It is also what keeps a database out of this test. Without the constraint the
    // request would bind and reach the handler, which would try to connect.
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("42")]
    public async Task A_delete_of_something_that_is_not_an_id_never_reaches_a_handler(string id)
    {
        using var app = SignedInApp();
        using var client = app.CreateNonFollowingClient();

        var response = await client.DeleteAsync(Path(id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_correction_of_something_that_is_not_an_id_never_reaches_a_handler()
    {
        using var response = await Put("not-a-guid", Body());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The literal routes registered on the same group must still win, and this is
    // the assertion that the new PUT and DELETE did not change that. Routing scores
    // a literal segment above a parameter and a constrained parameter does not match
    // a word in any case -- so this is belt and braces, and the belt is what would
    // silently break if the constraint were ever dropped from one of the three.
    //
    // 405 rather than 404: /api/transactions/import exists and takes POST, so
    // routing found it and refused the method. A 404 here would mean DELETE had
    // matched {id:guid} and the word "import" had been read as an id.
    [Fact]
    public async Task The_literal_routes_are_not_swallowed_by_the_id_parameter()
    {
        using var app = SignedInApp();
        using var client = app.CreateNonFollowingClient();

        var response = await client.DeleteAsync("/api/transactions/import");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // --- what the filter refuses ---------------------------------------------

    // **The test this file exists for.** Every one of these is a rule that lives on
    // CreateTransactionRequest and reaches this endpoint only through inheritance,
    // and every one of them is asserted on the wire rather than on a type -- so it
    // covers the filter being attached, the generic argument being the right one,
    // and Validator walking the base type, none of which reflection can see.
    //
    // A 400 also means the request stopped before AppDbContext, which is why these
    // can run at all.
    [Theory]
    [InlineData(0)]
    [InlineData(-12.50)]
    [InlineData(12.345)]
    public async Task An_amount_the_create_path_refuses_is_refused_here(decimal amount)
    {
        using var response = await Put(Guid.NewGuid(), Body(amount: amount));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("EU")]
    [InlineData("EURO")]
    public async Task A_currency_the_create_path_refuses_is_refused_here(string currency)
    {
        using var response = await Put(Guid.NewGuid(), Body(currency: currency));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_description_the_create_path_refuses_is_refused_here(string description)
    {
        using var response = await Put(Guid.NewGuid(), Body(description: description));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // PlausibleDateAttribute, which is the rule with a service lookup in it: it
    // reads a TimeProvider off ValidationContext.RequestServices, and
    // ValidationFilter<T> is what puts the request's services there. So this is
    // also the assertion that the filter builds its context with the two-argument
    // constructor rather than the one-argument one -- the difference #21 records,
    // which is invisible because the attribute falls back to the system clock and
    // stays correct.
    [Theory]
    [InlineData("2016-01-01")]
    [InlineData("2062-09-01")]
    public async Task A_date_the_create_path_refuses_is_refused_here(string occurredAt)
    {
        using var response = await Put(Guid.NewGuid(), Body(occurredAt: occurredAt));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // `required` on the record, enforced by System.Text.Json while binding, so this
    // never reaches the filter. Worth its own test because it is what stops a
    // half-filled body silently blanking a description or zeroing an amount -- a
    // PUT replaces, so an omitted field is the dangerous case rather than the
    // harmless one.
    [Fact]
    public async Task A_body_missing_a_field_is_refused_by_the_binder()
    {
        using var response = await Put(Guid.NewGuid(), new { description = "linella" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The keys are what put a message under the input it is about, and
    // ValidationFilter<T> camelCases them for exactly that. Asserted here rather
    // than trusted, because #52 records this going wrong on a real screen: a
    // message keyed "Password" was correct, visible, and in the banner at the top
    // instead of under the field.
    [Fact]
    public async Task A_refusal_names_the_field_the_way_the_form_names_it()
    {
        using var response = await Put(Guid.NewGuid(), Body(amount: -1));

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();

        Assert.NotNull(problem);
        Assert.True(problem.Errors.ContainsKey("amount"));
    }

    // --- helpers -------------------------------------------------------------

    private static async Task<HttpResponseMessage> Put(object id, object body)
    {
        using var app = SignedInApp();
        using var client = app.CreateNonFollowingClient();

        return await client.PutAsJsonAsync(Path(id), body);
    }

    // A body that would be stored if it reached the handler, so that every refusal
    // above is caused by the one field the test changed.
    private static object Body(
        string? occurredAt = null,
        decimal amount = 42.50m,
        string currency = "EUR",
        string description = "linella") =>
        new
        {
            occurredAt = occurredAt ?? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            amount,
            currency,
            description,
        };

    private sealed record ValidationProblem(Dictionary<string, string[]> Errors);
}
