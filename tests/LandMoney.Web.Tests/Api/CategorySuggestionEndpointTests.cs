using System.Net;
using System.Net.Http.Json;
using LandMoney.Web.Categorizing;
using LandMoney.Web.Tests.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace LandMoney.Web.Tests.Api;

/// <summary>#67, end to end -- the one endpoint here that can be.</summary>
// Every other handler in this application reaches AppDbContext, which is why
// AuthorizationTests stops at "the request was refused" and everything past that is
// checked by hand against the compose stack. This one touches no database, no disk
// and no clock: three fields in, one HTTP call out, an answer back. So the whole
// path -- routing, the authorization group, model binding, ValidationFilter, the
// handler, CategorizerClient and the JSON that leaves -- can be asserted in
// process, and the thing #67 is actually about (which of three shapes reaches the
// browser) is asserted against bytes rather than against a method's return value.
//
// **Two seams are replaced and they are replaced for opposite reasons.** The
// categorizer is stubbed because the point is to control what it answers. The
// authentication is stubbed because the alternative is UserManager, which is
// Postgres, which is the property CLAUDE.md defends -- these tests need no
// database, no Docker and no network. What that costs is written down at the end of
// the file. The authentication half moved to Auth/SignedIn.cs in #94, when the
// write endpoints needed the same door.
public class CategorySuggestionEndpointTests
{
    private const string Path = "/api/transactions/category-suggestion";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static TestApp SignedInApp(StubHandler? categorizer) => TestApp.With(services =>
    {
        SignedIn.AddTo(services);

        if (categorizer is null)
        {
            return;
        }

        // A second AddHttpClient for the same typed client adds to the
        // configuration rather than replacing it, and the last action wins -- so
        // this base address overrides the empty one TestApp sets, and the handler
        // below replaces the transport outright. TestApp's empty value is what
        // makes "no categorizer configured" the state this test gets when it
        // passes null.
        services.AddHttpClient<CategorizerClient>(client =>
                client.BaseAddress = new Uri("http://categorizer.test"))
            .ConfigurePrimaryHttpMessageHandler(() => categorizer);
    });

    private static async Task<HttpResponseMessage> Ask(
        StubHandler? categorizer,
        object? body = null)
    {
        using var app = SignedInApp(categorizer);
        using var client = app.CreateNonFollowingClient();

        return await client.PostAsJsonAsync(
            Path,
            body ?? new { amount = 42.50m, currency = "eur", description = "Dinner at the pizza place" });
    }

    private static StubHandler Answering(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task A_suggestion_comes_back_as_the_category_and_who_produced_it()
    {
        using var response = await Ask(Answering("""{"category":"eating-out","source":"rules"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CategorySuggestionBody>();

        Assert.NotNull(body);
        Assert.Equal("eating-out", body.Category);
        Assert.Equal("rules", body.Source);
    }

    [Fact]
    public async Task An_abstention_keeps_the_source_so_the_screen_can_say_who_had_no_idea()
    {
        // The state this endpoint exists to distinguish, asserted on the wire rather
        // than on a return value: a category of null with a source is "it answered,
        // and does not know", which #67 asks to be *shown* because it happens on
        // roughly a third of the labelled set.
        using var response = await Ask(Answering("""{"category":null,"source":"rules"}"""));

        var body = await response.Content.ReadFromJsonAsync<CategorySuggestionBody>();

        Assert.NotNull(body);
        Assert.Null(body.Category);
        Assert.Equal("rules", body.Source);
    }

    [Fact]
    public async Task No_categorizer_configured_is_a_200_with_nothing_in_it()
    {
        // And not a 5xx. Nothing failed: the question was answered, and the answer
        // is that there is no suggestion. The client renders this as nothing extra
        // beside the field, which is #67's third acceptance test -- and a status
        // code would have put a red line in the browser's console for the ordinary
        // state of an application whose categorizer is optional by design.
        using var response = await Ask(categorizer: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CategorySuggestionBody>();

        Assert.NotNull(body);
        Assert.Null(body.Category);
        Assert.Null(body.Source);
    }

    [Fact]
    public async Task A_categorizer_that_refuses_the_call_is_also_a_200_with_nothing_in_it()
    {
        // The promise #39 made for the save path, arriving where there is no
        // transaction to protect: a guess that failed may not become an error on a
        // screen. Indistinguishable from the test above on purpose -- both are
        // "there is no suggestion", and neither is something the person typing can
        // act on.
        using var response = await Ask(Answering("{}", HttpStatusCode.InternalServerError));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CategorySuggestionBody>();

        Assert.NotNull(body);
        Assert.Null(body.Source);
    }

    [Fact]
    public async Task The_categorizer_is_shown_the_currency_the_save_would_store()
    {
        // Uppercased before it is sent, exactly as the create path uppercases it
        // before storing it. It looks cosmetic and is not: the model is shown the
        // amount and the currency (prompt.py), and #65 keys its cache on that exact
        // string -- so a preview sending "eur" would miss the entry the save then
        // writes under "EUR", pay twice, and be free to answer differently from the
        // row that ends up on screen.
        var categorizer = Answering("""{"category":"eating-out","source":"rules"}""");

        using var response = await Ask(categorizer, new { amount = 42.50m, currency = "eur", description = "Dinner" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(categorizer.LastRequest);

        var sent = await categorizer.LastRequest.Content!.ReadAsStringAsync();

        Assert.Contains("\"EUR\"", sent);

        // And the description is sent as it was typed. Normalising it here would
        // make the preview and the save two different questions -- the same
        // mutation #39 caught by hand, in a new place.
        Assert.Contains("Dinner", sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task An_amount_the_categorizer_would_refuse_never_reaches_it(decimal amount)
    {
        // ValidationFilter answers before the handler runs, so nothing is sent. The
        // assertion that matters is the second one: a 400 here is this application
        // saying no, where without the rules it would be a 422 from a Python service
        // logged as the categorizer misbehaving.
        var categorizer = Answering("""{"category":"eating-out","source":"rules"}""");

        using var response = await Ask(
            categorizer, new { amount, currency = "EUR", description = "Dinner" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(categorizer.LastRequest);
    }

    [Fact]
    public async Task A_body_missing_a_field_is_refused_by_the_binder()
    {
        // `required` on the record, enforced by System.Text.Json while binding, so
        // this never reaches the filter. Worth its own test because the three
        // properties being `required` is what stops a partly-filled form producing a
        // suggestion about an amount of zero.
        var categorizer = Answering("""{"category":"eating-out","source":"rules"}""");

        using var response = await Ask(categorizer, new { description = "Dinner" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(categorizer.LastRequest);
    }

    private sealed record CategorySuggestionBody(string? Category, string? Source);

    // **What this does not check, said plainly.** The authentication is a stub, so
    // nothing here says a real cookie reaches this endpoint -- AuthorizationTests
    // asserts the refusal, which is the half that can be checked without a
    // database, and the accepting half is still verified by hand. Nothing here
    // touches the client's debounce or its aborts, which are the parts of #67 with
    // the interesting bug in them and which live in a language this suite does not
    // run. And a suggestion agreeing with what the save then stores is a property of
    // two calls to a service that is not here.
}
