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

/// <summary>Everything one call produced: who answered, and what they said.</summary>
// #67. The save path has never needed this -- an abstention and a dead service
// both mean "store no category", which is why <see cref="CategorySuggestion"/>?
// was enough for two issues. A suggestion shown while a description is typed
// needs them apart: the issue asks for "no idea" to be *visible*, because it is a
// normal answer on roughly a third of the labelled set, and for a categorizer
// that is not there to be *invisible*. Both are the same null in the database and
// the same null on the wire from the Python service, so the distinction has to be
// carried here or it is lost.
//
// Three states, and the source is what separates them:
//
//   Category and Source set -- a suggestion.
//   Source set, Category null -- something answered and declined to categorise.
//   both null -- there was no answer this application will use: nothing is
//     configured, nothing is there, it was too slow, or it broke its own contract.
//
// The last line is why an unusable body reports as no answer rather than as an
// abstention. A category over the column's width is not a declining service, but
// it is not an answer either, and "rules had no idea" is a sentence this
// application would be inventing.
public sealed record CategorizerAnswer(string? Category, string? Source)
{
    /// <summary>Nothing to be had, however that came about.</summary>
    public static readonly CategorizerAnswer Nothing = new(null, null);

    /// <summary>The suggestion, when there is a category and something to credit it to.</summary>
    // What the save path takes, and the reason that path did not have to change
    // for #67: it asks for the same shape it has always asked for, and the two
    // fields are non-null together or absent together the way #59 requires --
    // a category whose producer cannot be named is not stored.
    public CategorySuggestion? Suggestion =>
        Category is not null && Source is not null ? new CategorySuggestion(Category, Source) : null;
}

/// <summary>An answer, and the one word #64 filed the call under.</summary>
// #92. The sweep is the first caller that has to act on *why* there was no
// answer rather than only on the fact of it, and CategorizerAnswer deliberately
// cannot tell it: `Nothing` collapses "there is no categorizer", "it did not
// answer in time" and "it answered something unusable" into one value, which is
// exactly right for the two callers that only have to decide whether to store a
// category.
//
// It is not right for a retry. A row is retried until a cap, and the cap exists
// because every attempt that reaches the model is about 0.62 US cents (#87) --
// so the question the sweep asks is "could that attempt have cost anything",
// and CategorizerOutcome is the only thing in this application that knows.
// Widening the answer type instead would have put a word that means nothing to
// the create path onto the create path.
public sealed record CategorizerResult(CategorizerAnswer Answer, string Outcome);

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

    /// <summary>The suggestion for a transaction about to be stored, or null.</summary>
    /// <remarks>
    /// Null covers four different events and the caller treats them the same: the
    /// predictor abstained, the service answered something unusable, it could not
    /// name what produced the answer, or it was not there at all. Only the first is
    /// visible in a response, and nothing stores which it was.
    /// </remarks>
    // Kept at exactly the signature #39 gave it, although #67 made the inside
    // answer something richer. The create path wants a suggestion or nothing, and
    // handing it a three-state answer to unpack would put the question "did the
    // service answer" at a call site that has nothing to do with it.
    public async Task<CategorySuggestion?> SuggestCategoryAsync(
        string description,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
        => (await AskAsync(description, amount, currency, CategorizerKind.Save, cancellationToken))
            .Answer.Suggestion;

    /// <summary>What the categorizer says about a description being typed. #67.</summary>
    /// <remarks>
    /// Nothing here is stored, so this is the one caller that can tell an
    /// abstention from an absence and has a reason to: the first is shown as "no
    /// idea", which is a real answer, and the second is shown as nothing at all.
    /// </remarks>
    public Task<CategorizerAnswer> PreviewCategoryAsync(
        string description,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
        => AskAsync(description, amount, currency, CategorizerKind.Preview, cancellationToken)
            .ContinueWithAnswer();

    /// <summary>What the categorizer says about a row already in the database. #92.</summary>
    /// <remarks>
    /// The only caller that gets the outcome as well as the answer, because it is
    /// the only one that has to decide whether to ask again. See
    /// <see cref="CategorizerResult"/>.
    /// </remarks>
    // A third kind rather than reusing `save`, and the reason is that `save` would
    // quietly stop meaning what #64 recorded it as meaning. Until this change every
    // `save` call happened inside the request that wrote the row; from here none
    // do. Keeping the word would leave the summary reporting the same number for a
    // different event, which is the one failure a named vocabulary exists to
    // prevent -- and it would throw away the signal that this change took at all.
    // After #92 ships, `save=0` is correct and `save>0` means something still
    // categorises inline.
    public Task<CategorizerResult> SweepCategoryAsync(
        string description,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
        => AskAsync(description, amount, currency, CategorizerKind.Sweep, cancellationToken);

    // One body for both, and the two public methods above are the only places the
    // kind is chosen -- so a call site cannot label a save as a preview, which is
    // the mistake that would quietly make #64's numbers describe something else.
    private async Task<CategorizerResult> AskAsync(
        string description,
        decimal amount,
        string currency,
        string kind,
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
            metrics.Record(CategorizerOutcome.NotConfigured, source: null, kind, elapsed: null);
            return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.NotConfigured);
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
                    "Categorizer {Kind} {Outcome}: answered {StatusCode}; there is no category.",
                    kind, CategorizerOutcome.Refused, (int)response.StatusCode);
                metrics.Record(CategorizerOutcome.Refused, source: null, kind, Stopwatch.GetElapsedTime(started));
                return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Refused);
            }

            var body = await response.Content.ReadFromJsonAsync<CategorizeResponse>(Json, cancellationToken);

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
            //
            // **It moved above the abstention in #67, and the order is the
            // contract.** `source` is what says something answered at all, so it
            // has to be established before the answer is read -- an abstention
            // this application cannot attribute is indistinguishable from a
            // service that is not there, which is exactly the distinction the
            // preview path exists to make. What that changes, on a path
            // FastAPI cannot produce: a 200 carrying neither a category nor a
            // source is now `unusable` rather than `abstained`. That is the more
            // truthful of the two -- an answer with nothing in it is a broken
            // contract, not a predictor declining.
            if (body?.Source is not { Length: > 0 } source)
            {
                logger.LogWarning(
                    "Categorizer {Kind} {Outcome}: answered the category {Category} without naming a "
                    + "source; the answer is refused.",
                    kind, CategorizerOutcome.Unusable, body?.Category);
                metrics.Record(CategorizerOutcome.Unusable, source: null, kind, Stopwatch.GetElapsedTime(started));
                return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unusable);
            }

            // The same guard as the one above, for the same reason, against a
            // different column. Kept separate rather than folded into it so the log
            // line says which of the two was too long.
            if (source.Length > Transaction.CategorySourceMaxLength)
            {
                logger.LogWarning(
                    "Categorizer {Kind} {Outcome}: named a source of {Length} characters, over the {Max} "
                    + "the column holds; the answer is refused.",
                    kind, CategorizerOutcome.Unusable, source.Length, Transaction.CategorySourceMaxLength);
                // Deliberately not tagged with the source it sent. It is over a
                // hundred characters of something this application does not
                // recognise, and a metric dimension is the last place to put a
                // string another process chose -- #64's first trap, which is about
                // the description and is the same trap here.
                metrics.Record(CategorizerOutcome.Unusable, source: null, kind, Stopwatch.GetElapsedTime(started));
                return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unusable);
            }

            // Abstention. A 200 with a null category is the baseline working as
            // designed -- it declines on roughly a third of the labelled set -- so
            // it is not a warning and not an error.
            if (body.Category is not { Length: > 0 } category)
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
                //
                // **`source: null` although the source is known here, and returned
                // one line down.** The tally it feeds is rendered beside the
                // suggested count -- "8 suggested (rules=8)" -- so it answers "what
                // produced the categories", and counting declines in it would make
                // that line report more producers than categories. The word still
                // reaches the caller, which is where #67 needs it.
                metrics.Record(CategorizerOutcome.Abstained, source: null, kind, Stopwatch.GetElapsedTime(started));
                return new CategorizerResult(new CategorizerAnswer(null, source), CategorizerOutcome.Abstained);
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
                    "Categorizer {Kind} {Outcome}: answered a category of {Length} characters, over the {Max} "
                    + "the column holds; the answer is refused.",
                    kind, CategorizerOutcome.Unusable, category.Length, Transaction.CategoryMaxLength);
                metrics.Record(CategorizerOutcome.Unusable, source: null, kind, Stopwatch.GetElapsedTime(started));

                // Nothing, and not an abstention carrying the source. The service
                // did answer and could be named, so an argument for reporting
                // "{source} had no idea" exists -- and it would be this application
                // putting words in another process's mouth. It had an idea; this
                // side will not use it.
                //
                // #92: the *outcome* still says `unusable`, so the sweep can tell
                // this from a service that was never reached. Both are `Nothing` to
                // the two older callers and they are the opposite of each other to a
                // retry -- this one reached the model and may have been billed for.
                return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unusable);
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
                "Categorizer {Kind} {Outcome}: {Category} by {Source} in {ElapsedMs:F0}ms.",
                kind, CategorizerOutcome.Suggested, category, source, elapsed.TotalMilliseconds);
            metrics.Record(CategorizerOutcome.Suggested, source, kind, elapsed);
            return new CategorizerResult(new CategorizerAnswer(category, source), CategorizerOutcome.Suggested);
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
                "Categorizer {Kind} {Outcome}: no answer, gave up after {ElapsedMs:F0}ms (overall timeout "
                + "{Timeout}, so a shorter connect timeout fired if that is less). There is no category.",
                kind, CategorizerOutcome.Timeout, elapsed.TotalMilliseconds, http.Timeout);
            // #64 names this as the outcome easiest to label wrongly, and the
            // labelling is done by the `when` clause above rather than here. A
            // stopped container leaves the SYN unanswered instead of refusing it
            // (#39, measured), so it expires a clock and arrives on this branch --
            // "the categorizer is stopped" counts as three timeouts, not as three
            // unreachables, and that is the correct answer rather than a rounding
            // of one.
            metrics.Record(CategorizerOutcome.Timeout, source: null, kind, elapsed);
            return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Timeout);
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
            metrics.Record(CategorizerOutcome.Abandoned, source: null, kind, Stopwatch.GetElapsedTime(started));
            throw;
        }
        // Unreachable, refused, DNS failure, connection reset. The expected one
        // under compose is the service being stopped, which is the acceptance test
        // #39 names.
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "Categorizer {Kind} {Outcome}: there is no category.",
                kind, CategorizerOutcome.Unreachable);
            metrics.Record(CategorizerOutcome.Unreachable, source: null, kind, Stopwatch.GetElapsedTime(started));
            return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unreachable);
        }
        // A 200 whose body is not the contract: malformed JSON (JsonException), or
        // a content type the reader will not take, such as an HTML error page from
        // something sitting between here and there (NotSupportedException). Both
        // arrive only after a success status, so neither is caught above.
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogWarning(
                exception,
                "Categorizer {Kind} {Outcome}: the body was not the contract; there is no category.",
                kind, CategorizerOutcome.Unreadable);
            metrics.Record(CategorizerOutcome.Unreadable, source: null, kind, Stopwatch.GetElapsedTime(started));
            return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unreadable);
        }
    }
}

/// <summary>Drops the outcome for the two callers that have no use for it.</summary>
// So that #92 changes neither of the signatures #39 and #67 settled on. The save
// path wants a suggestion or nothing and the preview path wants the three-state
// answer; handing either of them a word about retries would put the sweep's
// concern at a call site that has none.
internal static class CategorizerResultExtensions
{
    public static async Task<CategorizerAnswer> ContinueWithAnswer(this Task<CategorizerResult> result)
        => (await result).Answer;
}
