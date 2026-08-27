using System.Net;
using System.Text;
using System.Text.Json;
using LandMoney.Web.Categorizing;
using LandMoney.Web.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LandMoney.Web.Tests.Categorizing;

/// <summary>
/// The one promise CategorizerClient makes: whatever the categorizer does, the
/// answer is a category or it is null -- never an exception on the save path.
/// </summary>
// These are the tests #39's "categorising must never block saving" needs in order
// to be a property rather than an intention. Every one of them is a way the other
// process can misbehave, and the assertion is always the same: null, and the
// caller carries on.
//
// #21 decided against Microsoft.AspNetCore.Mvc.Testing on the grounds that an
// IEndpointFilter is an ordinary object reachable without a server. The same
// reasoning applies here and lands in the same place for a different reason: an
// HttpClient is reachable without a network, because HttpMessageHandler is the
// seam. StubHandler below is about eight lines and replaces the entire transport,
// so a "timeout" test takes 50 milliseconds and an "unreachable" test needs
// nothing to be unreachable.
//
// What that leaves untested, said out loud the way #21 said it: that the client
// is registered in Program.cs at all, that its BaseAddress and Timeout come from
// configuration, and that the endpoint calls it. Those are checked by hand
// against the running compose stack -- the acceptance test in #39 -- and the day
// they need checking automatically is the day WebApplicationFactory earns its
// place.
public class CategorizerClientTests
{
    private const string Endpoint = "http://categorizer:8000";

    /// <summary>Replaces the transport. Every test here is a different answer from it.</summary>
    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }

    private static CategorizerClient ClientThat(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? timeout = null)
    {
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri(Endpoint),
            // Generous by default so a test never races the clock; the timeout
            // test passes its own.
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

        // NullLogger rather than a spy. What is asserted here is the return value,
        // and a test that also pinned the log message would fail on a reworded
        // sentence, which is not a behaviour change.
        return new CategorizerClient(http, NullLogger<CategorizerClient>.Instance);
    }

    private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static Task<CategorySuggestion?> Ask(CategorizerClient client, CancellationToken cancellationToken = default)
        => client.SuggestCategoryAsync("Dinner at the pizza place", 42.50m, "EUR", cancellationToken);

    // --- the two normal answers ----------------------------------------------

    [Fact]
    public async Task A_categorised_answer_comes_back_as_the_category_and_its_source()
    {
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"rules"}"""));

        Assert.Equal(new CategorySuggestion("eating-out", "rules"), await Ask(client));
    }

    [Fact]
    public async Task The_source_is_whatever_answered_and_not_a_name_this_side_chose()
    {
        // #59's rule that the truth about which code ran must not live in a
        // different file from the code that ran. Nothing here knows the word
        // "model" -- it arrives from the process that produced the answer, which
        // is why switching the predictor on the Python side needs no change here.
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"model"}"""));

        Assert.Equal(new CategorySuggestion("eating-out", "model"), await Ask(client));
    }

    [Fact]
    public async Task An_abstention_is_null_and_not_an_error()
    {
        // What the rules answer for roughly a third of the labelled set. It is a
        // 200: the service worked, and had nothing to say.
        var client = ClientThat((_, _) => Json("""{"category":null,"source":"rules"}"""));

        Assert.Null(await Ask(client));
    }

    // --- the ways the other process can misbehave ----------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]  // this side sent a body the service refused
    [InlineData(HttpStatusCode.NotFound)]             // the path moved
    [InlineData(HttpStatusCode.BadGateway)]           // something in between
    public async Task A_failure_status_is_null(HttpStatusCode status)
    {
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"rules"}""", status));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task An_unreachable_service_is_null()
    {
        // What `docker compose stop categorizer` produces, and the acceptance
        // test #39 names in as many words.
        var client = ClientThat((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Name or service not known (categorizer:8000)")));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_null()
    {
        var client = ClientThat((_, _) => Json("{not json"));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task A_body_that_is_not_the_contract_is_null()
    {
        // Valid JSON, wrong shape. Deserialises to a record whose Category is
        // null, which is the abstention branch rather than the JsonException one
        // -- included because the two arrive at the same answer by different
        // routes, and only one of them is a bug worth a log line.
        var client = ClientThat((_, _) => Json("""{"prediction":"eating-out"}"""));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task An_html_error_page_with_a_200_is_null()
    {
        // A proxy or a misrouted request. ReadFromJsonAsync refuses the content
        // type with NotSupportedException rather than JsonException, which is why
        // the client catches both -- and why this test exists separately from the
        // malformed one above.
        var client = ClientThat((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>502</body></html>", Encoding.UTF8, "text/html"),
        }));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task An_answer_longer_than_the_column_is_null()
    {
        // The guard that is not about the network. Stored, this would throw in
        // SaveChangesAsync and take the user's transaction down with a failed
        // guess about it -- the exact thing #39 forbids. One character over, so
        // the test fails if the comparison is ever written as >=.
        var overlong = new string('x', Transaction.CategoryMaxLength + 1);
        var client = ClientThat((_, _) =>
            Json($$"""{"category":"{{overlong}}","source":"rules"}"""));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task An_answer_exactly_as_long_as_the_column_is_kept()
    {
        var exact = new string('x', Transaction.CategoryMaxLength);
        var client = ClientThat((_, _) => Json($$"""{"category":"{{exact}}","source":"rules"}"""));

        Assert.Equal(new CategorySuggestion(exact, "rules"), await Ask(client));
    }

    // --- an answer that cannot say who produced it -- #59 ---------------------

    [Theory]
    [InlineData("""{"category":"eating-out"}""")]          // the field is absent
    [InlineData("""{"category":"eating-out","source":null}""")]
    [InlineData("""{"category":"eating-out","source":""}""")]
    public async Task A_category_whose_source_is_not_named_is_refused(string body)
    {
        // The guard that looks like over-caution and is not.
        // transactions.category_source exists because provenance cannot be
        // reconstructed after the fact, so storing this row would re-open the hole
        // the column was added to close -- one row at a time, invisibly, and only
        // for the rows written while the service was misbehaving. Refusing costs
        // one guess.
        //
        // Only reachable if the service breaks its own contract: contracts.py
        // declares `source` non-optional.
        var client = ClientThat((_, _) => Json(body));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task A_source_longer_than_the_column_is_refused()
    {
        // The same failure the overlong category has, against the narrower column:
        // stored, it throws in SaveChangesAsync and takes the user's transaction
        // with it. One character over, so the test fails if the comparison is ever
        // written as >=.
        var overlong = new string('x', Transaction.CategorySourceMaxLength + 1);
        var client = ClientThat((_, _) =>
            Json($$"""{"category":"eating-out","source":"{{overlong}}"}"""));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task A_source_exactly_as_long_as_the_column_is_kept()
    {
        var exact = new string('x', Transaction.CategorySourceMaxLength);
        var client = ClientThat((_, _) => Json($$"""{"category":"eating-out","source":"{{exact}}"}"""));

        Assert.Equal(new CategorySuggestion("eating-out", exact), await Ask(client));
    }

    [Fact]
    public async Task An_abstention_that_names_a_source_is_still_just_null()
    {
        // `{category: null, source: "model"}` is what the adapter answers when the
        // model declines, and there is nothing to store: a source with no category
        // would be a row claiming a producer for a value that does not exist. The
        // absence lives in the `?` on the return type rather than in two fields the
        // caller has to check against each other.
        var client = ClientThat((_, _) => Json("""{"category":null,"source":"model"}"""));

        Assert.Null(await Ask(client));
    }

    // --- no categorizer at all --------------------------------------------------

    [Fact]
    public async Task With_no_base_address_it_answers_null_without_calling_anything()
    {
        // What Program.cs produces when Categorizer:BaseUrl is absent -- which is
        // every run of efbundle, since it has no appsettings.json beside it. The
        // throw that used to be there instead is what failed the first deploy
        // after #39.
        //
        // The handler asserts rather than returns: the point is not only that the
        // answer is null but that nothing was sent. A placeholder base address
        // would also answer null, after paying the full timeout on every save.
        var called = false;
        var http = new HttpClient(new StubHandler((_, _) =>
        {
            called = true;
            return Json("""{"category":"eating-out","source":"rules"}""");
        }));
        // Deliberately not setting BaseAddress at all.
        var client = new CategorizerClient(http, NullLogger<CategorizerClient>.Instance);

        Assert.Null(await Ask(client));
        Assert.False(called);
    }

    // --- the clock ------------------------------------------------------------

    [Fact]
    public async Task A_service_that_never_answers_is_null_once_the_timeout_expires()
    {
        // The failure CLAUDE.md's "every network client gets a timeout" rule
        // exists for: without one this call hangs for as long as the other side
        // holds the connection, and an outage becomes a hang on the save path.
        var client = ClientThat(
            (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken)
                .ContinueWith(_ => new HttpResponseMessage(), TaskScheduler.Default),
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Null(await Ask(client));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed()
    {
        // The half of the `when` clause that is easy to get wrong, and the reason
        // the clause exists at all: HttpClient implements its timeout by
        // cancelling, so both of these surface as the same exception type. If the
        // client caught cancellation unconditionally it would return null here and
        // the handler would carry on and save a transaction for a request whose
        // caller has already gone.
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"rules"}"""));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ask(client, cts.Token));
    }

    // --- what goes out --------------------------------------------------------

    [Fact]
    public async Task The_request_is_the_contract_the_python_side_declares()
    {
        // contracts.py declares description, amount and currency, and pydantic
        // answers 422 for anything else -- which the client would then turn into a
        // silent null. So a mismatch here fails as "the categorizer never
        // categorises anything" with no error anywhere, which is why the wire
        // format is asserted rather than assumed.
        HttpRequestMessage? sent = null;
        string? body = null;

        var client = ClientThat(async (request, cancellationToken) =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return await Json("""{"category":"eating-out","source":"rules"}""");
        });

        await Ask(client);

        Assert.NotNull(sent);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{Endpoint}/categorize", sent.RequestUri?.ToString());

        using var json = JsonDocument.Parse(body!);
        Assert.Equal("Dinner at the pizza place", json.RootElement.GetProperty("description").GetString());
        Assert.Equal("EUR", json.RootElement.GetProperty("currency").GetString());

        // A JSON number, and exactly 42.50 rather than a float's neighbourhood of
        // it. contracts.py declares decimal_places=2, so an amount that had been
        // through a double would arrive as 42.499999999999996 and be refused.
        var amount = json.RootElement.GetProperty("amount");
        Assert.Equal(JsonValueKind.Number, amount.ValueKind);
        Assert.Equal(42.50m, amount.GetDecimal());
    }
}
