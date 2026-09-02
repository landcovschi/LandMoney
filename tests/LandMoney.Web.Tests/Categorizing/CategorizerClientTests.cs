using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using LandMoney.Web.Categorizing;
using LandMoney.Web.Models;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>The two bodies #92's sweep tests ask for, written once.</summary>
    // Ordinary escaped strings rather than the raw literals the rest of this file
    // uses. The sweep tests build one body by concatenation -- an over-long
    // category -- and a file that spells the same JSON two ways invites the two
    // from drifting apart.
    private const string Suggestion = "{\"category\":\"eating-out\",\"source\":\"rules\"}";
    private const string Abstention = "{\"category\":null,\"source\":\"rules\"}";

    /// <summary>The id the one-row batch below sends, and the id its answers carry.</summary>
    // #93. The sweep no longer asks about one row at a time, so every test in the
    // sweep section now goes through the batch endpoint -- which is the point rather
    // than a cost: the properties #92 asserted about a swept row have to survive the
    // change of transport, and they are asserted through it rather than beside it.
    private const string SweptRow = "row-1";

    private const string BatchSuggestion =
        "{\"answers\":[{\"id\":\"" + SweptRow + "\",\"category\":\"eating-out\",\"source\":\"rules\"}]}";

    private const string BatchAbstention =
        "{\"answers\":[{\"id\":\"" + SweptRow + "\",\"category\":null,\"source\":\"rules\"}]}";

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
        TimeSpan? timeout = null,
        CategorizerMetrics? metrics = null)
    {
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri(Endpoint),
            // Generous by default so a test never races the clock; the timeout
            // test passes its own.
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

        // #93. The batch call goes through a second HttpClient with its own budget,
        // and here it is the same stub behind both -- so one `respond` answers
        // whichever path a test asks for, and a test that means to exercise the batch
        // simply returns a batch body.
        //
        // The timeout is deliberately the same one rather than something larger. The
        // reason there are two clients in production is that eight seconds is wrong
        // for a batch; the reason there is one number here is that a test asserting
        // "it gave up" wants to control when, and a second knob would only mean two
        // ways to write the same test wrongly.
        var batch = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri(Endpoint),
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

        // NullLogger rather than a spy. What is asserted here is the return value
        // and the outcome recorded beside it, and a test that also pinned the log
        // message would fail on a reworded sentence, which is not a behaviour
        // change. The outcome word is the part that is promised to stay still --
        // that is what CategorizerOutcome is for -- and it is asserted through the
        // metrics rather than through the prose that carries it.
        return new CategorizerClient(
            http,
            new CategorizerBatchHttp(batch),
            metrics ?? NewMetrics(),
            NullLogger<CategorizerClient>.Instance);
    }

    /// <summary>A metrics instance of its own, so one test cannot see another's counts.</summary>
    // The provider is deliberately not disposed. IMeterFactory owns the Meter and
    // disposes it with the container, and instruments on a disposed Meter silently
    // stop recording -- which would turn every assertion below into "zero calls,
    // and the test passes only if it expected zero". A handful of undisposed
    // containers is what a test run can afford; a metric that records nothing and
    // says nothing is not.
    private static CategorizerMetrics NewMetrics(TimeProvider? time = null) =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            time ?? TimeProvider.System);

    private static long Counted(CategorizerWindow window, string outcome) =>
        window.ByOutcome.GetValueOrDefault(outcome);

    private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static Task<CategorySuggestion?> Ask(CategorizerClient client, CancellationToken cancellationToken = default)
        => client.SuggestCategoryAsync("Dinner at the pizza place", 42.50m, "EUR", cancellationToken);

    /// <summary>The same question down the #67 path, where the answer keeps its shape.</summary>
    private static Task<CategorizerAnswer> Preview(
        CategorizerClient client, CancellationToken cancellationToken = default)
        => client.PreviewCategoryAsync("Dinner at the pizza place", 42.50m, "EUR", cancellationToken);

    /// <summary>The #92 path, the only one that gets the outcome as well as the answer.</summary>
    // A batch of one since #93, unwrapped back to the single result the assertions in
    // that section were written against. The unwrapping is what those tests are
    // entitled to assume: when the call itself failed there are no rows, and when it
    // did not there is exactly one entry per row that was sent.
    private static async Task<CategorizerResult> Sweep(
        CategorizerClient client, CancellationToken cancellationToken = default)
    {
        var result = await SweepMany(client, cancellationToken, Row(SweptRow));

        return result.CallFailure is { } failure
            ? new CategorizerResult(CategorizerAnswer.Nothing, failure)
            : result.Rows[SweptRow];
    }

    /// <summary>The #93 path as the sweep really calls it: many rows, one call.</summary>
    private static Task<CategorizerBatchResult> SweepMany(
        CategorizerClient client, CancellationToken cancellationToken, params CategorizerBatchRow[] rows)
        => client.SweepCategoriesAsync(rows, cancellationToken);

    private static CategorizerBatchRow Row(string id) =>
        new(id, "Dinner at the pizza place", 42.50m, "EUR");

    /// <summary>A client with nothing configured, which is a legal state since #57.</summary>
    // Not reachable through ClientThat: that one sets a base address, which is the
    // whole difference between "the categorizer said nothing" and "there is no
    // categorizer". The handler throws rather than answering, so a test that
    // accidentally sent a request would fail loudly instead of passing.
    private static CategorizerClient UnconfiguredClient(CategorizerMetrics metrics)
    {
        static HttpClient Silent() => new(new StubHandler(
            (_, _) => throw new InvalidOperationException("No request should be sent.")));

        return new CategorizerClient(
            Silent(), new CategorizerBatchHttp(Silent()), metrics, NullLogger<CategorizerClient>.Instance);
    }

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
        var metrics = NewMetrics();
        var client = new CategorizerClient(
            http,
            new CategorizerBatchHttp(new HttpClient(new StubHandler((_, _) => Json(Suggestion)))),
            metrics,
            NullLogger<CategorizerClient>.Instance);

        Assert.Null(await Ask(client));
        Assert.False(called);

        // #64: counted under its own name, and not timed. This is what the deployed
        // application did on every save between #39 and #61 -- an absent
        // categorizer, storing nothing, reporting nothing -- so the whole value of
        // the outcome is that it is a number somebody can look at.
        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.NotConfigured));

        // No call was made, so there is no duration to attribute to it. A zero here
        // would be an instant success in the histogram, dragging every percentile
        // towards nothing.
        Assert.Equal(0, window.Measured);
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

    // --- what it counts -- #64 ------------------------------------------------
    //
    // Every test above asserts that the answer is null; none of them can tell you
    // *why*, which is the whole of what #64 is about. On the wire an abstention, a
    // dead service and a body nobody can read are one value. These are the tests
    // that keep them apart.

    [Fact]
    public async Task A_suggestion_is_counted_under_the_source_that_produced_it()
    {
        var metrics = NewMetrics();
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"model"}"""), metrics: metrics);

        await Ask(client);

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.Suggested));
        Assert.Equal(1, window.BySource["model"]);

        // The call was timed, which is what makes a p95 possible at all.
        Assert.Equal(1, window.Measured);
    }

    [Fact]
    public async Task An_abstention_is_not_counted_as_a_failure()
    {
        // #64's third acceptance test, and the reason the outcome vocabulary exists
        // rather than a boolean. Both of these answer null; one is the baseline
        // declining on a row it was never going to know, the other is a service that
        // is not there. Counting them together would produce a "failure rate" of a
        // third that nobody could act on.
        var metrics = NewMetrics();

        await Ask(ClientThat((_, _) => Json("""{"category":null,"source":"rules"}"""), metrics: metrics));
        await Ask(ClientThat(
            (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")),
            metrics: metrics));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(2, window.Calls);
        Assert.Equal(1, Counted(window, CategorizerOutcome.Abstained));
        Assert.Equal(1, Counted(window, CategorizerOutcome.Unreachable));

        // And neither is the other. Written as two assertions rather than one
        // because the failure this is guarding against is a later refactor folding
        // the two branches together, which would show up as a 2 on one line and a 0
        // on the other -- a test asserting only inequality would still pass.
        Assert.Equal(0, Counted(window, CategorizerOutcome.Suggested));
        Assert.Equal(0, Counted(window, CategorizerOutcome.Timeout));
    }

    [Fact]
    public async Task A_service_that_never_answers_counts_as_a_timeout_and_never_as_unreachable()
    {
        // **#64's first acceptance test**, and the one it says is easy to get
        // wrong: stop the categorizer, save three transactions, and the numbers must
        // say three timeouts rather than three unreachables. #39 measured why -- a
        // stopped container leaves the SYN unanswered instead of refusing it, so the
        // failure is a clock expiring and not a connection being refused.
        //
        // The stub reproduces that shape rather than the symptom: it never answers,
        // and the client's own timeout is what ends the call.
        var metrics = NewMetrics();
        var client = ClientThat(
            (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken)
                .ContinueWith(_ => new HttpResponseMessage(), TaskScheduler.Default),
            timeout: TimeSpan.FromMilliseconds(50),
            metrics: metrics);

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await Ask(client));
        }

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(3, Counted(window, CategorizerOutcome.Timeout));
        Assert.Equal(0, Counted(window, CategorizerOutcome.Unreachable));

        // The three of them are in the latency figures too. A p95 computed only over
        // the calls that worked is exactly the number that hides a timeout, which is
        // #64's second trap.
        Assert.Equal(3, window.Measured);
        Assert.True(window.P95Ms >= 50, $"p95 was {window.P95Ms}ms, which cannot include a 50ms timeout.");
    }

    [Fact]
    public async Task A_refused_status_and_an_unreadable_body_are_different_numbers()
    {
        // Two of the four things that can be wrong with an answer, and they send a
        // reader to different places: a 502 is something between here and there, a
        // body that will not parse is a contract that has moved.
        var metrics = NewMetrics();

        await Ask(ClientThat((_, _) => Json("{}", HttpStatusCode.BadGateway), metrics: metrics));
        await Ask(ClientThat((_, _) => Json("{not json"), metrics: metrics));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.Refused));
        Assert.Equal(1, Counted(window, CategorizerOutcome.Unreadable));
    }

    [Theory]
    [InlineData("""{"category":"eating-out"}""")]
    [InlineData("""{"category":"eating-out","source":""}""")]
    public async Task An_answer_that_breaks_the_contract_is_counted_as_unusable(string body)
    {
        // Not "unreadable": the body parsed perfectly. The service answered
        // something this application refuses to store, which is a bug on one side
        // or the other and not a network event -- and it is invisible in a count
        // that only knows about exceptions, because nothing was thrown.
        var metrics = NewMetrics();

        Assert.Null(await Ask(ClientThat((_, _) => Json(body), metrics: metrics)));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.Unusable));
        Assert.Equal(0, Counted(window, CategorizerOutcome.Unreadable));
    }

    [Fact]
    public async Task A_caller_who_gives_up_is_counted_separately_and_still_gets_its_exception()
    {
        // The call was made and paid for, so it is counted; the exception still
        // escapes, because saving a transaction for a request whose caller has gone
        // is what the `when` clause exists to prevent. Kept apart from Timeout
        // because a number rising here is a fact about the browser's ten-second
        // budget rather than about the categorizer.
        var metrics = NewMetrics();
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"rules"}"""), metrics: metrics);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ask(client, cts.Token));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.Abandoned));
        Assert.Equal(0, Counted(window, CategorizerOutcome.Timeout));
    }

    [Fact]
    public async Task A_source_this_application_does_not_know_does_not_become_a_new_dimension()
    {
        // #64's first trap, one field along from the description it is written
        // about: `source` is a string another process chooses, so tagging it
        // verbatim would let a misbehaving service mint a time series per request.
        // The answer is still stored and the log line still names it -- only the
        // dimension is bounded.
        var metrics = NewMetrics();
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"wizard"}"""), metrics: metrics);

        Assert.Equal(new CategorySuggestion("eating-out", "wizard"), await Ask(client));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, window.BySource["other"]);
        Assert.False(window.BySource.ContainsKey("wizard"));
    }

    // --- the preview path (#67) ----------------------------------------------

    [Fact]
    public async Task A_previewed_suggestion_is_the_category_and_the_source()
    {
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"rules"}"""));

        Assert.Equal(new CategorizerAnswer("eating-out", "rules"), await Preview(client));
    }

    [Fact]
    public async Task A_previewed_abstention_says_who_had_no_idea()
    {
        // **The distinction this whole path exists for.** On the save side an
        // abstention and a dead service are both null and are treated the same,
        // correctly -- neither stores a category. Here they are two different
        // things on a screen: "no idea" is a normal answer on roughly a third of
        // the labelled set and has to be visible, and a categorizer that is not
        // running has to be invisible, because there is nothing the person typing
        // could do about it.
        var answer = await Preview(ClientThat((_, _) => Json("""{"category":null,"source":"rules"}""")));

        Assert.Equal(new CategorizerAnswer(null, "rules"), answer);

        // And it is still not a suggestion, so the save path cannot accidentally
        // store one.
        Assert.Null(answer.Suggestion);
    }

    [Fact]
    public async Task A_preview_with_no_categorizer_configured_answers_nothing_at_all()
    {
        // The state the deployed application was in on every save between #39 and
        // #61. It has to be distinguishable from an abstention here, or the screen
        // would tell somebody the categorizer had no idea about a description it
        // was never shown.
        var metrics = NewMetrics();

        Assert.Equal(CategorizerAnswer.Nothing, await Preview(UnconfiguredClient(metrics)));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.NotConfigured));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_preview_that_fails_answers_nothing_and_never_throws(HttpStatusCode status)
    {
        // The promise #39 made for the save path, restated for a path that has no
        // transaction to protect: a suggestion may not become an error on a screen.
        // Both fields null, which is what the client renders as nothing extra.
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"rules"}""", status));

        Assert.Equal(CategorizerAnswer.Nothing, await Preview(client));
    }

    [Fact]
    public async Task An_answer_this_side_refuses_is_not_reported_as_an_abstention()
    {
        // A category over the column's width, from a service that named itself
        // perfectly well. It is tempting to report it as "rules had no idea",
        // because the source is right there -- and that would be this application
        // inventing a sentence. It had an idea; this side will not use it.
        var metrics = NewMetrics();
        var tooLong = new string('x', 101);
        var client = ClientThat(
            (_, _) => Json($$"""{"category":"{{tooLong}}","source":"rules"}"""), metrics: metrics);

        Assert.Equal(CategorizerAnswer.Nothing, await Preview(client));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.Unusable));
        Assert.Equal(0, Counted(window, CategorizerOutcome.Abstained));
    }

    [Fact]
    public async Task A_preview_is_counted_as_a_preview_and_a_save_as_a_save()
    {
        // #67. The outcomes are one set of counters because a preview fails in the
        // same nine ways; the call counts are two, because from here on the
        // previews are the majority and against the model each is a charge. The
        // kind is chosen by which method was called and by nothing else, so this is
        // also the assertion that the two entry points did not end up sharing one.
        var metrics = NewMetrics();
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"rules"}"""), metrics: metrics);

        await Ask(client);
        await Preview(client);
        await Preview(client);

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, window.ByKind[CategorizerKind.Save]);
        Assert.Equal(2, window.ByKind[CategorizerKind.Preview]);
        Assert.Equal(3, Counted(window, CategorizerOutcome.Suggested));
    }

    [Fact]
    public async Task A_preview_that_is_abandoned_still_throws_and_is_still_counted()
    {
        // The typing stopped, or the field changed, and the browser aborted. The
        // exception escapes for the same reason it does on the save path -- nobody
        // is left to answer -- and the call is counted, because it was made and
        // against the model it was paid for. That number is what says how much of
        // the bill was thrown away.
        var metrics = NewMetrics();
        var client = ClientThat((_, _) => Json("""{"category":"eating-out","source":"rules"}"""), metrics: metrics);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Preview(client, cts.Token));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.Abandoned));
        Assert.Equal(1, window.ByKind[CategorizerKind.Preview]);
    }

    // --- the sweep, #92 ------------------------------------------------------

    // The reason this path exists at all. The save path gets a suggestion or null
    // and the preview path gets the three-state answer; neither can tell "there is
    // no categorizer" from "it answered something unusable", because
    // CategorizerAnswer.Nothing collapses them on purpose. A retry has to tell them
    // apart, because only one of the two could have been billed for.
    [Fact]
    public async Task The_sweep_is_told_why_there_was_no_answer()
    {
        var tooLong = "{\"answers\":[{\"id\":\"" + SweptRow + "\",\"category\":\""
            + new string('x', 101) + "\",\"source\":\"rules\"}]}";
        var unreachable = ClientThat((_, _) => throw new HttpRequestException("no route"));
        var unusable = ClientThat((_, _) => Json(tooLong));

        var whenNothingIsThere = await Sweep(unreachable);
        var whenTheAnswerIsRefused = await Sweep(unusable);

        // Indistinguishable to the two older callers...
        Assert.Equal(CategorizerAnswer.Nothing, whenNothingIsThere.Answer);
        Assert.Equal(CategorizerAnswer.Nothing, whenTheAnswerIsRefused.Answer);

        // ...and opposite to this one. The first is free and must not be charged
        // against the cap; the second reached the model and must be.
        Assert.Equal(CategorizerOutcome.Unreachable, whenNothingIsThere.Outcome);
        Assert.Equal(CategorizerOutcome.Unusable, whenTheAnswerIsRefused.Outcome);
        Assert.False(CategorizerOutcome.CountsAgainstTheCap(whenNothingIsThere.Outcome));
        Assert.True(CategorizerOutcome.CountsAgainstTheCap(whenTheAnswerIsRefused.Outcome));
    }

    [Fact]
    public async Task A_swept_row_that_gets_a_category_reports_it_with_its_source()
    {
        var result = await Sweep(ClientThat((_, _) => Json(BatchSuggestion)));

        Assert.Equal(CategorizerOutcome.Suggested, result.Outcome);
        Assert.Equal(new CategorySuggestion("eating-out", "rules"), result.Answer.Suggestion);
    }

    // An abstention is a final answer, which is what lets the sweep stop owing a
    // category for the row rather than asking the same question again at the same
    // price. The source is what distinguishes it from silence -- #67's rule,
    // unchanged, and the one the sweep leans on hardest.
    [Fact]
    public async Task A_swept_row_the_categorizer_declines_is_still_an_answer()
    {
        var result = await Sweep(ClientThat((_, _) => Json(BatchAbstention)));

        Assert.Equal(CategorizerOutcome.Abstained, result.Outcome);
        Assert.Null(result.Answer.Suggestion);
        Assert.Equal("rules", result.Answer.Source);
    }

    // The state the deployed application was in on every save between #39 and #61,
    // and the one #92 must not make invisible again: with no categorizer configured
    // the sweep charges nothing and stops the tick, so rows keep owing a category
    // indefinitely. That is correct, and it is only findable because this outcome
    // has a name.
    [Fact]
    public async Task With_no_categorizer_configured_the_sweep_is_told_so_and_charged_nothing()
    {
        var metrics = NewMetrics();

        var result = await Sweep(UnconfiguredClient(metrics));

        Assert.Equal(CategorizerOutcome.NotConfigured, result.Outcome);
        Assert.Equal(CategorizerAnswer.Nothing, result.Answer);
        Assert.False(CategorizerOutcome.CountsAgainstTheCap(result.Outcome));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, window.ByKind[CategorizerKind.Sweep]);
    }

    // So that #64's summary keeps meaning what it says. The call the sweep makes is
    // the one that used to be a `save`, and counting it as one would report the
    // same number for a different event -- and would hide the fact that nothing
    // categorises inline any more.
    [Fact]
    public async Task A_swept_row_is_counted_as_a_sweep_and_never_as_a_save()
    {
        var metrics = NewMetrics();
        var client = ClientThat((_, _) => Json(BatchSuggestion), metrics: metrics);

        await Sweep(client);

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, window.ByKind[CategorizerKind.Sweep]);
        Assert.Equal(0, window.ByKind.GetValueOrDefault(CategorizerKind.Save));
    }

    // The two older signatures are unchanged by #92, which is the whole point of
    // the outcome being dropped for them rather than added to what they return.
    [Fact]
    public async Task The_save_and_preview_paths_still_answer_exactly_what_they_did()
    {
        var client = ClientThat((_, _) => Json(Suggestion));

        Assert.Equal(new CategorySuggestion("eating-out", "rules"), await Ask(client));
        Assert.Equal(new CategorizerAnswer("eating-out", "rules"), await Preview(client));
    }

    // --- the batch, #93 ------------------------------------------------------

    // **The test this whole shape exists for.** #93's last trap is a batch that
    // answers positionally: drop one row and every answer after it belongs to its
    // neighbour, which is wrong data, stored, with nothing about it that looks wrong
    // afterwards. So the stub deliberately answers in the *reverse* order and gives
    // each row a different category -- the one arrangement in which a positional
    // implementation is caught rather than passing by luck.
    [Fact]
    public async Task Each_row_gets_the_answer_that_names_it_and_never_its_neighbours()
    {
        var client = ClientThat((_, _) => Json(
            """
            {"answers":[
              {"id":"c","category":"health","source":"rules"},
              {"id":"a","category":"groceries","source":"rules"},
              {"id":"b","category":"transport","source":"model"}
            ]}
            """));

        var result = await SweepMany(client, default, Row("a"), Row("b"), Row("c"));

        Assert.Null(result.CallFailure);
        Assert.Equal(new CategorySuggestion("groceries", "rules"), result.Rows["a"].Answer.Suggestion);
        Assert.Equal(new CategorySuggestion("transport", "model"), result.Rows["b"].Answer.Suggestion);
        Assert.Equal(new CategorySuggestion("health", "rules"), result.Rows["c"].Answer.Suggestion);
    }

    // The Python side is allowed to answer fewer rows than it was sent -- a row whose
    // predictor raised is left out, so the rest keep the answers they were already
    // paid for. Every row that was sent still gets an entry here, so the sweep never
    // has to decide what a missing key means.
    [Fact]
    public async Task A_row_the_batch_did_not_answer_is_unusable_and_still_owed()
    {
        var client = ClientThat((_, _) => Json(
            """{"answers":[{"id":"a","category":"groceries","source":"rules"}]}"""));

        var result = await SweepMany(client, default, Row("a"), Row("b"));

        Assert.Equal(CategorizerOutcome.Suggested, result.Rows["a"].Outcome);

        // Not `abstained`, which is the distinction that decides whether the row is
        // ever asked about again: an abstention is a final answer and takes the row
        // out of the queue for ever. Nothing answered about this one.
        Assert.Equal(CategorizerOutcome.Unusable, result.Rows["b"].Outcome);
        Assert.Equal(CategorizerAnswer.Nothing, result.Rows["b"].Answer);
        Assert.True(CategorizerOutcome.CountsAgainstTheCap(result.Rows["b"].Outcome));
    }

    // An answer with no id cannot be paired with anything, and guessing which row it
    // belonged to is the bug the ids exist to make impossible. The row it was meant
    // for reports as unanswered, which is the loud version of the same fact.
    [Fact]
    public async Task An_answer_with_no_id_is_dropped_rather_than_given_to_a_row()
    {
        var client = ClientThat((_, _) => Json(
            """{"answers":[{"category":"groceries","source":"rules"}]}"""));

        var result = await SweepMany(client, default, Row("a"));

        Assert.Equal(CategorizerOutcome.Unusable, result.Rows["a"].Outcome);
    }

    // A row that was never sent cannot be written to, and the entries that were sent
    // are unaffected by one arriving. It is worth a log line and nothing more.
    [Fact]
    public async Task An_answer_for_a_row_that_was_not_sent_is_ignored()
    {
        var client = ClientThat((_, _) => Json(
            """
            {"answers":[
              {"id":"a","category":"groceries","source":"rules"},
              {"id":"somebody-elses-row","category":"health","source":"rules"}
            ]}
            """));

        var result = await SweepMany(client, default, Row("a"));

        Assert.Equal(CategorizerOutcome.Suggested, result.Rows["a"].Outcome);
        Assert.Single(result.Rows);
    }

    // **A failure of the call is not a failure of any row in it**, which is the one
    // place #93 could have changed what #92 measured and deliberately does not. The
    // sweep charges only the oldest row for this; here the assertion is that it is
    // given what it needs to -- one word about the call, and no rows at all, rather
    // than twenty per-row failures it would have to recognise as one event.
    [Fact]
    public async Task A_batch_that_fails_as_a_whole_reports_one_reason_and_no_rows()
    {
        var client = ClientThat((_, _) => throw new HttpRequestException("no route"));

        var result = await SweepMany(client, default, Row("a"), Row("b"), Row("c"));

        Assert.Equal(CategorizerOutcome.Unreachable, result.CallFailure);
        Assert.Empty(result.Rows);
    }

    // #64's numbers count questions about transactions, and a batch asks as many
    // questions as it carries rows. Counting a failed batch of three as one timeout
    // would leave "3 timed out" reading as "1", silently -- the same failure
    // CategorizerKind was invented to prevent, one field along.
    [Fact]
    public async Task A_failed_batch_is_counted_once_for_every_row_it_carried()
    {
        var metrics = NewMetrics();
        var client = ClientThat((_, _) => throw new HttpRequestException("no route"), metrics: metrics);

        await SweepMany(client, default, Row("a"), Row("b"), Row("c"));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(3, Counted(window, CategorizerOutcome.Unreachable));
        Assert.Equal(3, window.ByKind[CategorizerKind.Sweep]);
    }

    [Fact]
    public async Task A_batch_that_answers_is_counted_once_for_every_row_it_answered()
    {
        var metrics = NewMetrics();
        var client = ClientThat(
            (_, _) => Json(
                """
                {"answers":[
                  {"id":"a","category":"groceries","source":"rules"},
                  {"id":"b","category":null,"source":"rules"}
                ]}
                """),
            metrics: metrics);

        await SweepMany(client, default, Row("a"), Row("b"));

        var window = metrics.TakeWindow();
        Assert.NotNull(window);
        Assert.Equal(1, Counted(window, CategorizerOutcome.Suggested));
        Assert.Equal(1, Counted(window, CategorizerOutcome.Abstained));
        Assert.Equal(2, window.ByKind[CategorizerKind.Sweep]);
    }

    // Nothing owed is the ordinary state of this application and is not an event. The
    // Python side refuses a request that asks nothing, so sending one would be a
    // `refused` counted every five seconds -- which would make the one line that is
    // supposed to mean something is wrong mean nothing at all.
    [Fact]
    public async Task An_empty_batch_sends_nothing_and_counts_nothing()
    {
        var metrics = NewMetrics();
        var called = false;
        var client = ClientThat(
            (_, _) =>
            {
                called = true;
                return Json(BatchSuggestion);
            },
            metrics: metrics);

        var result = await client.SweepCategoriesAsync([], default);

        Assert.False(called);
        Assert.Null(result.CallFailure);
        Assert.Empty(result.Rows);
        Assert.Null(metrics.TakeWindow());
    }

    // The batch goes to its own path, and the per-row calls do not. Getting this
    // wrong would be a 404 or a 422 on every tick, which is `refused` -- a word that
    // reads as the categorizer misbehaving rather than as a URL typed here.
    [Fact]
    public async Task The_batch_and_the_single_row_call_go_to_different_paths()
    {
        var paths = new List<string>();
        var client = ClientThat((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Json(request.RequestUri!.AbsolutePath.EndsWith("batch", StringComparison.Ordinal)
                ? BatchSuggestion
                : Suggestion);
        });

        await Ask(client);
        await Sweep(client);

        Assert.Equal(["/categorize", "/categorize/batch"], paths);
    }
}
