using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using LandMoney.Web.Models;

namespace LandMoney.Web.Categorizing;

/// <summary>A category, and the name of whatever produced it.</summary>
// Not in CategorizerContracts.cs with the other two records, and the split is the
// point: those describe bytes on a wire and may be shaped by a service this
// application does not own, while this is what *this* application decided to
// believe. Both fields are non-nullable here because a suggestion that cannot name
// its producer is refused rather than represented -- so the absence lives in the
// `?` on the return type, in one place, instead of in two fields the caller has to
// check separately.
public sealed record CategorySuggestion(string Category, string Source);

/// <summary>Asks the Python categorizer for a category, and never lets it stop a save.</summary>
// A typed client -- registered with AddHttpClient&lt;CategorizerClient&gt;, so the
// HttpClient handed in is a short-lived wrapper over a pooled, rotated
// HttpMessageHandler. What that buys is the thing a `new HttpClient()` inside a
// handler gets wrong in both directions at once: one per request exhausts
// sockets, and one static instance never notices a DNS change. Under compose the
// second is not academic -- `categorizer` resolves to whatever address the
// container has after it is recreated.
//
// **This class exists to fail quietly.** #39: categorising must never block
// saving, because a transaction is the user's data and a category is a guess
// about it. Every failure below is caught, logged and turned into null, which is
// why Category has been nullable since #1. The only exception it lets past is the
// caller's own cancellation -- see the `when` clause.
//
// **Every exit below records an outcome -- #64.** There are nine of them and only
// four are exceptions, which is the half of that issue easiest to miss: an
// abstention, a refused status and an answer that breaks the contract are all
// ordinary returns, and counting only the `catch` blocks would leave the normal
// case invisible and the abstention indistinguishable from a failure. The words
// are `CategorizerOutcome`'s and never free text, so the same event is called the
// same thing in the log line, in the metric tag and in the summary.
public sealed class CategorizerClient(
    HttpClient http, CategorizerMetrics metrics, ILogger<CategorizerClient> logger)
{
    // The path is absolute on purpose. HttpClient resolves a relative path
    // against BaseAddress with URI rules rather than string concatenation: with a
    // base of "http://categorizer:8000/api" and a relative "categorize", the last
    // segment of the base is *replaced* and "/api" is silently gone. A leading
    // slash builds the request from the authority alone, which is the behaviour
    // that does not depend on whether someone remembered a trailing slash in
    // configuration.
    private const string Path = "/categorize";

    // JsonSerializerDefaults.Web is what ASP.NET Core uses: camelCase names and
    // case-insensitive reads. The System.Net.Http.Json helpers already default to
    // it, so this is written out rather than relied upon -- the default is a fact
    // about the library's version, and the contract is a fact about the service.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The suggestion, or null if there is not one to be had.</summary>
    /// <remarks>
    /// Null covers four different events and the caller treats them the same: the
    /// predictor abstained, the service answered something unusable, it could not
    /// name what produced the answer, or it was not there at all. Only the first is
    /// visible in a response, and nothing stores which it was.
    /// </remarks>
    public async Task<CategorySuggestion?> SuggestCategoryAsync(
        string description,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
    {
        // No base address means no categorizer is configured, which Program.cs
        // treats as a legal state rather than a startup failure -- see the long
        // comment there, and the deploy it broke. Without this check the call
        // below throws InvalidOperationException ("An invalid request URI was
        // provided"), which is not one of the three the catch blocks expect, so
        // it would escape and turn a create request into a 500. That is the exact
        // shape this class exists to prevent, arriving through configuration
        // instead of through the network.
        if (http.BaseAddress is null)
        {
            // No duration, because nothing was timed: this is the one outcome
            // where no call was made. Recording a zero instead would fill the
            // histogram with instant successes and pull every percentile down,
            // which is the shape of lie #64's second trap is about.
            //
            // It is counted rather than passed over in silence, and this is the
            // number most worth having: it is what the deployed application did on
            // every single save between #39 and #61, and nothing anywhere reported
            // it. A figure on this line separates "the categorizer answers nothing"
            // from "there is no categorizer".
            metrics.Record(CategorizerOutcome.NotConfigured, source: null, elapsed: null);
            return null;
        }

        // Read on every path since #64 -- a latency figure that covered only the
        // calls that succeeded would hide the two-second connect timeout, which is
        // the one the p95 exists to show. It arrived for the timeout path, because
        // #59 gave this client two clocks. Without it the log names `http.Timeout` and is wrong
        // whenever the *connect* budget was the one that fired -- measured: a save
        // against an unreachable categorizer gave up after 2.15 s and reported
        // "did not answer within 00:00:08". A log line that misnames which limit
        // was hit sends the reader to the wrong configuration key.
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await http.PostAsJsonAsync(
                Path, new CategorizeRequest(description, amount, currency), Json, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately not read as a problem document. A 422 here means
                // this application sent a body the service refused, which is a bug
                // on this side rather than anything to show a user -- the status
                // is what a log needs in order to find it.
                logger.LogWarning(
                    "Categorizer {Outcome}: answered {StatusCode}; storing the transaction with no category.",
                    CategorizerOutcome.Refused, (int)response.StatusCode);
                metrics.Record(CategorizerOutcome.Refused, source: null, Stopwatch.GetElapsedTime(started));
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<CategorizeResponse>(Json, cancellationToken);

            // Abstention. A 200 with a null category is the baseline working as
            // designed -- it declines on roughly a third of the labelled set -- so
            // it is not a warning and not an error.
            if (body?.Category is not { Length: > 0 } category)
            {
                // Counted, and counted under its own name. On the wire this is the
                // same `null` a dead service produces, and #64's third acceptance
                // test is that the two numbers must never be one number: a
                // categorizer that declines everything and a categorizer that is
                // not answering look identical from the database and are entirely
                // different problems.
                //
                // Still no log line. An abstention is the baseline working as
                // designed on roughly a third of the labelled set, and a warning
                // per save would be noise that trains a reader to skip the ones
                // that matter. The count is the record.
                metrics.Record(CategorizerOutcome.Abstained, source: null, Stopwatch.GetElapsedTime(started));
                return null;
            }

            // The one guard here that is not about the network, and the one that
            // would otherwise break the promise this class exists for.
            // Transaction.Category is MaxLength(100); a service answering
            // something longer would make SaveChangesAsync throw, and the
            // transaction the user typed would be lost to a failed guess about it.
            // Refusing the answer keeps the failure inside the part that is
            // allowed to fail.
            if (category.Length > Transaction.CategoryMaxLength)
            {
                logger.LogWarning(
                    "Categorizer {Outcome}: answered a category of {Length} characters, over the {Max} "
                    + "the column holds; storing the transaction with no category.",
                    CategorizerOutcome.Unusable, category.Length, Transaction.CategoryMaxLength);
                metrics.Record(CategorizerOutcome.Unusable, source: null, Stopwatch.GetElapsedTime(started));
                return null;
            }

            // #59. A category whose producer cannot be named is refused outright,
            // and this is the guard that most looks like over-caution and is not.
            // transactions.category_source exists because provenance cannot be
            // reconstructed afterwards -- so storing a category with an unknown
            // source would re-open, one row at a time, the exact hole the column
            // was added to close. Refusing costs one guess; storing costs the
            // ability to ever say which code wrote that row.
            //
            // Note this is reachable only if the service breaks its own contract:
            // contracts.py declares `source` non-optional and FastAPI will not
            // serialise a response without it.
            if (body.Source is not { Length: > 0 } source)
            {
                logger.LogWarning(
                    "Categorizer {Outcome}: answered the category {Category} without naming a source; "
                    + "storing the transaction with no category.",
                    CategorizerOutcome.Unusable, category);
                metrics.Record(CategorizerOutcome.Unusable, source: null, Stopwatch.GetElapsedTime(started));
                return null;
            }

            // The same guard as the one above, for the same reason, against a
            // different column. Kept separate rather than folded into it so the log
            // line says which of the two was too long.
            if (source.Length > Transaction.CategorySourceMaxLength)
            {
                logger.LogWarning(
                    "Categorizer {Outcome}: named a source of {Length} characters, over the {Max} "
                    + "the column holds; storing the transaction with no category.",
                    CategorizerOutcome.Unusable, source.Length, Transaction.CategorySourceMaxLength);
                // Deliberately not tagged with the source it sent. It is over a
                // hundred characters of something this application does not
                // recognise, and a metric dimension is the last place to put a
                // string another process chose -- #64's first trap, which is about
                // the description and is the same trap here.
                metrics.Record(CategorizerOutcome.Unusable, source: null, Stopwatch.GetElapsedTime(started));
                return null;
            }

            // Stored verbatim, neither trimmed nor lower-cased, which is the same
            // treatment Category gets one guard up. Normalising here would make this
            // application the author of a value another process chose, and the
            // vocabulary is already closed at the end that owns it -- `Source` is a
            // StrEnum in contracts.py, so `Model` cannot leave that service.
            // {Source} verbatim here and bounded in the metric, which is the split
            // #64's cardinality trap forces: a value another process chose may be
            // read in a log line, where one more distinct string costs one more
            // line, and may not become a dimension, where it costs one more series
            // for ever. CategorizerMetrics.Label is where the bounding happens.
            var elapsed = Stopwatch.GetElapsedTime(started);
            logger.LogInformation(
                "Categorizer {Outcome}: {Category} by {Source} in {ElapsedMs:F0}ms.",
                CategorizerOutcome.Suggested, category, source, elapsed.TotalMilliseconds);
            metrics.Record(CategorizerOutcome.Suggested, source, elapsed);
            return new CategorySuggestion(category, source);
        }
        // The timeout, and recognising it takes the `when` clause. HttpClient
        // implements Timeout by cancelling the request, so what surfaces is
        // TaskCanceledException -- the same exception the caller's own token
        // produces, which is why this is a famously confusing thing to read in a
        // log. The token is what tells them apart: if it is not cancelled, nobody
        // asked for this, so it was the clock.
        //
        // The other half matters more than the message. When the token *is*
        // cancelled the caller has gone, and swallowing that here would carry on
        // and save a transaction for a request that no longer exists.
        //
        // **Both clocks arrive here, which is why the elapsed time is logged and
        // not just the limit.** SocketsHttpHandler.ConnectTimeout implements its
        // expiry by cancelling too, so a service that is not there and a service
        // that is thinking too long are the same exception on the same branch --
        // measured in #59, where the connect budget fired at 2.15 s and the only
        // number in the message said eight seconds. The elapsed time is what tells
        // a reader which of Categorizer:ConnectTimeoutSeconds and
        // Categorizer:TimeoutSeconds to go and look at.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            logger.LogWarning(
                "Categorizer {Outcome}: no answer, gave up after {ElapsedMs:F0}ms (overall timeout {Timeout}, "
                + "so a shorter connect timeout fired if that is less). Storing the transaction with no category.",
                CategorizerOutcome.Timeout, elapsed.TotalMilliseconds, http.Timeout);
            // #64 names this as the outcome easiest to label wrongly, and the
            // labelling is done by the `when` clause above rather than here. A
            // stopped container leaves the SYN unanswered instead of refusing it
            // (#39, measured), so it expires a clock and arrives on this branch --
            // "the categorizer is stopped" counts as three timeouts, not as three
            // unreachables, and that is the correct answer rather than a rounding
            // of one.
            metrics.Record(CategorizerOutcome.Timeout, source: null, elapsed);
            return null;
        }
        // The caller went away. The exception is rethrown -- saving a transaction
        // for a request that no longer exists is what the clause above exists to
        // prevent -- and counted on the way past, because the call was made and
        // paid for. A number that rises here is a fact about the browser client's
        // ten-second budget or about somebody closing a tab, and not about the
        // categorizer; separating it from a timeout is what stops it being read as
        // one.
        catch (OperationCanceledException)
        {
            metrics.Record(CategorizerOutcome.Abandoned, source: null, Stopwatch.GetElapsedTime(started));
            throw;
        }
        // Unreachable, refused, DNS failure, connection reset. The expected one
        // under compose is the service being stopped, which is the acceptance test
        // #39 names.
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "Categorizer {Outcome}: storing the transaction with no category.",
                CategorizerOutcome.Unreachable);
            metrics.Record(CategorizerOutcome.Unreachable, source: null, Stopwatch.GetElapsedTime(started));
            return null;
        }
        // A 200 whose body is not the contract: malformed JSON (JsonException), or
        // a content type the reader will not take, such as an HTML error page from
        // something sitting between here and there (NotSupportedException). Both
        // arrive only after a success status, so neither is caught above.
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogWarning(
                exception,
                "Categorizer {Outcome}: the body was not the contract; storing the transaction with no category.",
                CategorizerOutcome.Unreadable);
            metrics.Record(CategorizerOutcome.Unreadable, source: null, Stopwatch.GetElapsedTime(started));
            return null;
        }
    }
}
