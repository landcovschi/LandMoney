using System.Net;
using LandMoney.Web.Tests.Auth;

namespace LandMoney.Web.Tests.Api;

/// <summary>What the list and the summary refuse, end to end. #95.</summary>
// **These are the only two things about #95's endpoints a test in this suite can
// reach, and the reason is worth stating rather than apologising for.** Both
// handlers go on to query AppDbContext, which is the wall AuthorizationTests and #62
// both document -- but both refuse a bad parameter *before* they touch it, so
// routing, the authorization group, model binding of the query string, the check and
// the ProblemDetails that leaves are all real here. It is the same window #67's
// endpoint fits through entirely.
//
// What that leaves out is everything about the rows: that a page is fifty long, that
// the cursor lands where it should, and that the summary adds up the month. Those
// need Postgres and were checked by hand against the compose stack; the write-up is
// in the pull request and in CLAUDE.md.
//
// Authentication is stubbed rather than performed, because the alternative is
// UserManager, which is Postgres, which is the property #22 defends.
public class TransactionListEndpointTests
{
    // A 400 rather than an empty page, and this is the assertion that keeps it one.
    // An empty page is what the end of the list looks like, so answering a broken
    // cursor with zero rows would tell a client it had read everything -- the one
    // wrong answer that is indistinguishable from a right one.
    [Theory]
    [InlineData("nonsense")]
    [InlineData("bm90LWEtY3Vyc29y")]
    [InlineData("!!!!")]
    public async Task A_cursor_this_API_did_not_issue_is_refused(string cursor)
    {
        var response = await Get($"/api/transactions?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // An empty cursor is *not* a broken one: `?cursor=` is what a client sends when it
    // builds the query string from a variable it did not set, and the honest reading
    // is "no cursor". It reaches the handler and is answered with the first page --
    // which here means it gets past the check and fails on the database instead,
    // since there is not one. Asserting "not 400" is the whole of what this suite can
    // say, and it is what distinguishes the two paths.
    [Fact]
    public async Task An_empty_cursor_is_read_as_no_cursor_rather_than_a_broken_one()
    {
        var response = await Get("/api/transactions?cursor=");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // A limit is clamped and never refused, which is the asymmetry with the cursor
    // above: a limit of a million names a real intention this server declines to
    // honour in full, and two hundred rows is a complete answer to it. TransactionPagingTests
    // holds the numbers; this holds the status code.
    [Theory]
    [InlineData("1000000")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task A_limit_out_of_range_is_clamped_and_not_refused(string limit)
    {
        var response = await Get($"/api/transactions?limit={limit}");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // A limit that is not a number at all is a different case and is the binder's:
    // `int?` cannot take "many", so this is a 400 before the handler is entered. It
    // is here so that the theory above is read as being about *range* rather than
    // about anything a client can put in that parameter.
    [Fact]
    public async Task A_limit_that_is_not_a_number_is_refused_by_the_binder()
    {
        var response = await Get("/api/transactions?limit=many");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The month is required rather than defaulted to the server's own clock, and this
    // is what says so: a summary with no month is a 400 and never "whatever month it
    // is here". OccurredAt is a plain day with no zone (#17), so the server's month
    // is somebody else's month for most of the world.
    [Theory]
    [InlineData("/api/transactions/summary")]
    [InlineData("/api/transactions/summary?month=")]
    [InlineData("/api/transactions/summary?month=2026")]
    [InlineData("/api/transactions/summary?month=2026-13")]
    [InlineData("/api/transactions/summary?month=2026-8")]
    [InlineData("/api/transactions/summary?month=2026-08-19")]
    public async Task A_summary_without_a_month_it_can_read_is_refused(string path)
    {
        var response = await Get(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // And a month it can read gets past the check, which is what stops the theory
    // above from passing over a handler that refuses everything.
    [Fact]
    public async Task A_month_it_can_read_is_not_refused()
    {
        var response = await Get("/api/transactions/summary?month=2026-08");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The count and the mark are one collection asked about the two ways HTTP has, so
    // both verbs answer on one path. This is the assertion that the GET was actually
    // registered rather than falling through to the catch-all -- a 405 or a 404 here
    // would leave the button with no number and nothing saying why.
    [Fact]
    public async Task The_backfill_count_is_a_route_and_not_a_missing_one()
    {
        var response = await Get("/api/transactions/backfill-categories");

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Get(string path)
    {
        using var app = TestApp.With(SignedIn.AddTo);
        using var client = app.CreateNonFollowingClient();

        return await client.GetAsync(path);
    }
}
