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

/// <summary>One row of a batch, as this application knows it. #93.</summary>
// The id is the transaction's, and it is the whole of what makes a batch safe:
// #93's last trap is an answer paired with a row by position, which after one
// dropped row is every subsequent transaction categorised as its neighbour --
// silently, and in a way nothing about the data would later reveal.
//
// Its own record rather than the wire type, for the reason CategorizeRequest is not
// CreateTransactionRequest: one is what this application asks about, the other is
// what happens to be on a wire this application does not own.
public sealed record CategorizerBatchRow(string Id, string Description, decimal Amount, string Currency);

/// <summary>What one batch call produced: one result per row, or one reason for none.</summary>
// Two things a retry has to tell apart, and collapsing them is what #92's cap would
// get wrong.
//
// <see cref="CallFailure"/> is null when the call itself worked, whatever the
// answers said. When it is set, <see cref="Rows"/> is empty: nothing was answered,
// and the failure is a property of the *call* rather than of any row in it -- so
// charging every row an attempt for it would abandon a whole backlog over an
// outage that says nothing about any particular row. CategorizerSweep is where that
// argument is spelled out and applied.
//
// When the call worked, <see cref="Rows"/> holds exactly one entry per row that was
// sent, keyed by the id it was sent under -- including the rows the service did not
// answer for, which are `unusable`. A caller therefore never has to decide what a
// missing key means, because there are none.
public sealed record CategorizerBatchResult(
    string? CallFailure,
    IReadOnlyDictionary<string, CategorizerResult> Rows)
{
    /// <summary>No rows were owed, so no call was made and nothing failed.</summary>
    public static readonly CategorizerBatchResult NothingAsked =
        new(null, new Dictionary<string, CategorizerResult>());
}

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
    HttpClient http,
    CategorizerBatchHttp batch,
    CategorizerMetrics metrics,
    ILogger<CategorizerClient> logger)
{
    // The path is absolute on purpose. HttpClient resolves a relative path
    // against BaseAddress with URI rules rather than string concatenation: with a
    // base of "http://categorizer:8000/api" and a relative "categorize", the last
    // segment of the base is *replaced* and "/api" is silently gone. A leading
    // slash builds the request from the authority alone, which is the behaviour
    // that does not depend on whether someone remembered a trailing slash in
    // configuration.
    private const string Path = "/categorize";

    /// <summary>Many rows in one call. #93.</summary>
    private const string BatchPath = "/categorize/batch";

    // The two overall budgets, named so a log line can say which one ran out. #59
    // paid for a message that named the wrong clock, and #93's fourth trap is the
    // same mistake with a second budget available to name wrongly: eight seconds is
    // right for one row and nowhere near right for a hundred.
    private const string TimeoutKey = "Categorizer:TimeoutSeconds";
    private const string BatchTimeoutKey = "Categorizer:BatchTimeoutSeconds";

    /// <summary>What a batch call that failed as a whole answers about its rows.</summary>
    private static readonly IReadOnlyDictionary<string, CategorizerResult> EmptyRows =
        new Dictionary<string, CategorizerResult>();

    /// <summary>A body, or the one word for why there is not one. #93.</summary>
    // A private shape rather than a tuple, because the invariant is worth a name:
    // exactly one of Body and Failure is meaningful, and Elapsed is null on both
    // failure paths -- SendAsync has already recorded those, so a caller that used
    // it would be counting the same call twice.
    private readonly record struct Sent<TResponse>(TResponse? Body, string? Failure, TimeSpan? Elapsed);

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

    /// <summary>What the categorizer says about rows already in the database. #93.</summary>
    /// <remarks>
    /// The only caller that gets outcomes as well as answers, because it is the only
    /// one that has to decide whether to ask again. See <see cref="CategorizerBatchResult"/>.
    /// </remarks>
    // **This replaced a per-row SweepCategoryAsync rather than joining it**, and the
    // reason is that a batch of one is a batch. Two sweep paths would be two things
    // to keep in step -- two timeouts, two ways of counting, two places the retry
    // rule is applied -- for a saving of one list allocation on the day a single row
    // is owed a category.
    //
    // What a batch buys is not the round trip. Twenty HTTP requests to a service on
    // the same network cost a few milliseconds more than one; twenty *model* calls at
    // about 2.1 s each (#60) cost forty-two seconds, and the Python side runs them
    // concurrently. #62's imported rows are the reason that matters: a three-hundred
    // row file drains in about two minutes instead of ten.
    public async Task<CategorizerBatchResult> SweepCategoriesAsync(
        IReadOnlyList<CategorizerBatchRow> rows,
        CancellationToken cancellationToken)
    {
        const string kind = CategorizerKind.Sweep;

        // Nothing owed is not an event. Sending an empty batch would be a 422 -- the
        // Python side refuses it, on the grounds that a request asking nothing is a
        // caller with a bug -- and counting a `refused` for it would put a number on
        // the one line that is supposed to mean something is wrong.
        if (rows.Count == 0)
        {
            return CategorizerBatchResult.NothingAsked;
        }

        var sent = await SendAsync<CategorizeBatchRequest, CategorizeBatchResponse>(
            batch.Client,
            BatchPath,
            BatchTimeoutKey,
            new CategorizeBatchRequest(
                [.. rows.Select(row => new CategorizeBatchItem(
                    row.Id, row.Description, row.Amount, row.Currency))]),
            kind,

            // **Once per row, not once per call, and that is what keeps #64's numbers
            // meaning what they meant.** Until this change one call was one question
            // about one transaction, so "8 timed out" meant eight rows got no
            // category. Counting a failed batch of twenty as one timeout would leave
            // the same sentence describing something twenty times smaller, silently
            // -- which is the exact failure CategorizerKind was invented to prevent,
            // one field along. The unit of these counters is a question about a
            // transaction, and a batch asks as many questions as it carries rows.
            (outcome, elapsed) => RecordForEachOf(rows, outcome, kind, elapsed),
            cancellationToken);

        if (sent.Failure is { } failure)
        {
            return new CategorizerBatchResult(failure, EmptyRows);
        }

        return new CategorizerBatchResult(null, Pair(rows, sent.Body, sent.Elapsed, kind));
    }

    /// <summary>Matches every row that was sent to the answer that names it.</summary>
    // The pairing is by id and never by position, which is #93's last trap: a batch
    // that answers positionally and drops a row shifts every answer after it, and
    // that shows up much later as one transaction categorised as its neighbour. The
    // Python side is allowed to return fewer answers than there were items -- a row
    // whose predictor raised is left out so the rest keep the answers they were
    // already paid for -- so a missing row is an expected state here rather than a
    // parse failure.
    private IReadOnlyDictionary<string, CategorizerResult> Pair(
        IReadOnlyList<CategorizerBatchRow> rows,
        CategorizeBatchResponse? body,
        TimeSpan? elapsed,
        string kind)
    {
        var answers = new Dictionary<string, CategorizeBatchAnswer>(StringComparer.Ordinal);

        foreach (var answer in body?.Answers ?? [])
        {
            // An answer with no id cannot be paired with anything. Dropping it is
            // loud in exactly the way that matters -- the row it was meant for
            // reports as unanswered below -- where guessing which row it belonged to
            // is the bug this whole shape exists to make impossible.
            if (answer.Id is not { Length: > 0 } id)
            {
                logger.LogWarning(
                    "Categorizer {Kind}: an answer in the batch carried no id, so there is no row to "
                    + "give it to.",
                    kind);
                continue;
            }

            // TryAdd rather than an assignment, so a repeated id keeps the first
            // answer rather than the last. Neither is right -- the request cannot
            // contain a repeated id, because the rows come from a primary key -- so
            // what matters is that it is decided here rather than by iteration order.
            if (!answers.TryAdd(id, answer))
            {
                logger.LogWarning(
                    "Categorizer {Kind}: the batch answered the same row twice; the second answer is "
                    + "ignored.",
                    kind);
            }
        }

        var results = new Dictionary<string, CategorizerResult>(rows.Count, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            CategorizerResult result;

            if (answers.Remove(row.Id, out var answer))
            {
                result = Interpret(new CategorizeResponse(answer.Category, answer.Source), kind);
            }
            else
            {
                // Counted as `unusable` and not as an abstention, which is the
                // distinction #67 established and this is the third caller to need:
                // an abstention is a final answer and would take the row out of the
                // queue for ever. Nothing answered about this row, so it is still
                // owed one.
                logger.LogWarning(
                    "Categorizer {Kind} {Outcome}: the batch of {Count} came back with no answer for "
                    + "one of its rows; that row is still owed a category.",
                    kind, CategorizerOutcome.Unusable, rows.Count);
                result = new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unusable);
            }

            Record(result, kind, elapsed);
            results[row.Id] = result;
        }

        // Whatever is left was answered and never asked about. It is not this
        // application's row and there is nothing to do with it, but it means the two
        // sides disagree about what was sent -- which is worth a line, because every
        // other symptom of that is silence.
        if (answers.Count > 0)
        {
            logger.LogWarning(
                "Categorizer {Kind}: the batch answered {Count} rows that were not sent to it.",
                kind, answers.Count);
        }

        return results;
    }

    // One body for both single-row callers, and the two public methods above are the
    // only places the kind is chosen -- so a call site cannot label a save as a
    // preview, which is the mistake that would quietly make #64's numbers describe
    // something else.
    private async Task<CategorizerResult> AskAsync(
        string description,
        decimal amount,
        string currency,
        string kind,
        CancellationToken cancellationToken)
    {
        var sent = await SendAsync<CategorizeRequest, CategorizeResponse>(
            http,
            Path,
            TimeoutKey,
            new CategorizeRequest(description, amount, currency),
            kind,
            (outcome, elapsed) => metrics.Record(outcome, source: null, kind, elapsed),
            cancellationToken);

        // The call itself failed. SendAsync has already logged it and counted it, and
        // there is no body to make anything of.
        if (sent.Failure is { } failure)
        {
            return new CategorizerResult(CategorizerAnswer.Nothing, failure);
        }

        var result = Interpret(sent.Body, kind);
        Record(result, kind, sent.Elapsed);

        return result;
    }

    /// <summary>Sends one request and gets a body back, or the one word for why not.</summary>
    // Extracted in #93 so that the batch path and the per-row path share their four
    // `catch` blocks rather than owning one each. That sharing is the point rather
    // than the line count: the words in those blocks are CategorizerOutcome's, and
    // two copies of a vocabulary are two copies that drift.
    //
    // **It records the failures and never the successes**, and the split is exactly
    // the question a batch raises. A call that fails is one event whose meaning the
    // caller knows -- one row, or twenty -- so the caller passes in how to count it.
    // A call that succeeds is a body somebody still has to make sense of, and only
    // the caller knows whether that is one answer or a hundred.
    private async Task<Sent<TResponse>> SendAsync<TRequest, TResponse>(
        HttpClient client,
        string path,
        string budgetKey,
        TRequest body,
        string kind,
        Action<string, TimeSpan?> record,
        CancellationToken cancellationToken)
    {
        // No base address means no categorizer is configured, which Program.cs
        // treats as a legal state rather than a startup failure -- see the long
        // comment there, and the deploy it broke. Without this check the call below
        // throws InvalidOperationException ("An invalid request URI was provided"),
        // which is not one of the three the catch blocks expect, so it would escape
        // and turn a create request into a 500. That is the exact shape this class
        // exists to prevent, arriving through configuration instead of through the
        // network.
        if (client.BaseAddress is null)
        {
            // No duration, because nothing was timed: this is the one outcome where
            // no call was made. Recording a zero instead would fill the histogram
            // with instant successes and pull every percentile down, which is the
            // shape of lie #64's second trap is about.
            //
            // It is counted rather than passed over in silence, and this is the
            // number most worth having: it is what the deployed application did on
            // every single save between #39 and #61, and nothing anywhere reported
            // it. A figure on this line separates "the categorizer answers nothing"
            // from "there is no categorizer".
            record(CategorizerOutcome.NotConfigured, null);
            return new Sent<TResponse>(default, CategorizerOutcome.NotConfigured, null);
        }

        // Read on every path since #64 -- a latency figure that covered only the
        // calls that succeeded would hide the two-second connect timeout, which is
        // the one the p95 exists to show.
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await client.PostAsJsonAsync(path, body, Json, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately not read as a problem document. A 422 here means this
                // application sent a body the service refused, which is a bug on this
                // side rather than anything to show a user -- the status is what a
                // log needs in order to find it. On the batch path it is also what a
                // sweep configured past MAX_BATCH_ITEMS looks like from here, which
                // is why CategorizerSweep clamps rather than trusting the number.
                logger.LogWarning(
                    "Categorizer {Kind} {Outcome}: answered {StatusCode}; there is no category.",
                    kind, CategorizerOutcome.Refused, (int)response.StatusCode);
                record(CategorizerOutcome.Refused, Stopwatch.GetElapsedTime(started));
                return new Sent<TResponse>(default, CategorizerOutcome.Refused, null);
            }

            return new Sent<TResponse>(
                await response.Content.ReadFromJsonAsync<TResponse>(Json, cancellationToken),
                null,
                Stopwatch.GetElapsedTime(started));
        }
        // The timeout, and recognising it takes the `when` clause. HttpClient
        // implements Timeout by cancelling the request, so what surfaces is
        // TaskCanceledException -- the same exception the caller's own token
        // produces, which is why this is a famously confusing thing to read in a
        // log. The token is what tells them apart: if it is not cancelled, nobody
        // asked for this, so it was the clock.
        //
        // The other half matters more than the message. When the token *is*
        // cancelled the caller has gone, and swallowing that here would carry on and
        // save a transaction for a request that no longer exists.
        //
        // **Both clocks arrive here, which is why the elapsed time is logged and not
        // just the limit.** SocketsHttpHandler.ConnectTimeout implements its expiry
        // by cancelling too, so a service that is not there and a service that is
        // thinking too long are the same exception on the same branch -- measured in
        // #59, where the connect budget fired at 2.15 s and the only number in the
        // message said eight seconds. **And there are now two overall budgets rather
        // than one** -- the batch waits far longer than a save ever did -- so the
        // message names the key that set the one in force as well as the number,
        // which is what #93's fourth trap asks for.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            logger.LogWarning(
                "Categorizer {Kind} {Outcome}: no answer, gave up after {ElapsedMs:F0}ms ({BudgetKey} is "
                + "{Timeout}, so a shorter connect timeout fired if that is less). There is no category.",
                kind, CategorizerOutcome.Timeout, elapsed.TotalMilliseconds, budgetKey, client.Timeout);

            // #64 names this as the outcome easiest to label wrongly, and the
            // labelling is done by the `when` clause above rather than here. A
            // stopped container leaves the SYN unanswered instead of refusing it
            // (#39, measured), so it expires a clock and arrives on this branch --
            // "the categorizer is stopped" counts as timeouts, not as unreachables,
            // and that is the correct answer rather than a rounding of one.
            record(CategorizerOutcome.Timeout, elapsed);
            return new Sent<TResponse>(default, CategorizerOutcome.Timeout, null);
        }
        // The caller went away. The exception is rethrown -- saving a transaction for
        // a request that no longer exists is what the clause above exists to prevent
        // -- and counted on the way past, because the call was made and paid for. A
        // number that rises here is a fact about the browser client's ten-second
        // budget or about somebody closing a tab, and not about the categorizer;
        // separating it from a timeout is what stops it being read as one.
        catch (OperationCanceledException)
        {
            record(CategorizerOutcome.Abandoned, Stopwatch.GetElapsedTime(started));
            throw;
        }
        // Unreachable, refused, DNS failure, connection reset. The expected one under
        // compose is the service being stopped, which is the acceptance test #39
        // names.
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "Categorizer {Kind} {Outcome}: there is no category.",
                kind, CategorizerOutcome.Unreachable);
            record(CategorizerOutcome.Unreachable, Stopwatch.GetElapsedTime(started));
            return new Sent<TResponse>(default, CategorizerOutcome.Unreachable, null);
        }
        // A 200 whose body is not the contract: malformed JSON (JsonException), or a
        // content type the reader will not take, such as an HTML error page from
        // something sitting between here and there (NotSupportedException). Both
        // arrive only after a success status, so neither is caught above.
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogWarning(
                exception,
                "Categorizer {Kind} {Outcome}: the body was not the contract; there is no category.",
                kind, CategorizerOutcome.Unreadable);
            record(CategorizerOutcome.Unreadable, Stopwatch.GetElapsedTime(started));
            return new Sent<TResponse>(default, CategorizerOutcome.Unreadable, null);
        }
    }

    /// <summary>What this application makes of one answer it was given.</summary>
    // Every guard that used to sit inline in AskAsync, unchanged and now shared with
    // the batch path -- which is the point of extracting it. A rule about what may be
    // stored has to hold whichever call carried the answer, and a second copy for the
    // batch would be a second place to forget the column's width.
    //
    // It records nothing and logs the refusals, so that the caller decides how many
    // times an outcome counts.
    private CategorizerResult Interpret(CategorizeResponse? body, string kind)
    {
        // #59. A category whose producer cannot be named is refused outright, and
        // this is the guard that most looks like over-caution and is not.
        // transactions.category_source exists because provenance cannot be
        // reconstructed afterwards -- so storing a category with an unknown source
        // would re-open, one row at a time, the exact hole the column was added to
        // close. Refusing costs one guess; storing costs the ability to ever say
        // which code wrote that row.
        //
        // Note this is reachable only if the service breaks its own contract:
        // contracts.py declares `source` non-optional and FastAPI will not serialise
        // a response without it.
        //
        // **It moved above the abstention in #67, and the order is the contract.**
        // `source` is what says something answered at all, so it has to be
        // established before the answer is read -- an abstention this application
        // cannot attribute is indistinguishable from a service that is not there,
        // which is exactly the distinction the preview path exists to make. What that
        // changes, on a path FastAPI cannot produce: a 200 carrying neither a
        // category nor a source is `unusable` rather than `abstained`. That is the
        // more truthful of the two -- an answer with nothing in it is a broken
        // contract, not a predictor declining.
        if (body?.Source is not { Length: > 0 } source)
        {
            logger.LogWarning(
                "Categorizer {Kind} {Outcome}: answered the category {Category} without naming a "
                + "source; the answer is refused.",
                kind, CategorizerOutcome.Unusable, body?.Category);
            return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unusable);
        }

        // The same guard as the one above, for the same reason, against a different
        // column. Kept separate rather than folded into it so the log line says which
        // of the two was too long.
        if (source.Length > Transaction.CategorySourceMaxLength)
        {
            logger.LogWarning(
                "Categorizer {Kind} {Outcome}: named a source of {Length} characters, over the {Max} "
                + "the column holds; the answer is refused.",
                kind, CategorizerOutcome.Unusable, source.Length, Transaction.CategorySourceMaxLength);
            return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unusable);
        }

        // Abstention. A 200 with a null category is the baseline working as designed
        // -- it declines on roughly a third of the labelled set -- so it is not a
        // warning and not an error.
        //
        // Still no log line. A warning per save would be noise that trains a reader
        // to skip the ones that matter; the count is the record, and #64's third
        // acceptance test is that this count and the timeout count must never be one
        // number.
        if (body.Category is not { Length: > 0 } category)
        {
            return new CategorizerResult(new CategorizerAnswer(null, source), CategorizerOutcome.Abstained);
        }

        // The one guard here that is not about the network, and the one that would
        // otherwise break the promise this class exists for. Transaction.Category is
        // MaxLength(100); a service answering something longer would make
        // SaveChangesAsync throw, and the transaction the user typed would be lost to
        // a failed guess about it. Refusing the answer keeps the failure inside the
        // part that is allowed to fail.
        if (category.Length > Transaction.CategoryMaxLength)
        {
            logger.LogWarning(
                "Categorizer {Kind} {Outcome}: answered a category of {Length} characters, over the {Max} "
                + "the column holds; the answer is refused.",
                kind, CategorizerOutcome.Unusable, category.Length, Transaction.CategoryMaxLength);

            // Nothing, and not an abstention carrying the source. The service did
            // answer and could be named, so an argument for reporting "{source} had
            // no idea" exists -- and it would be this application putting words in
            // another process's mouth. It had an idea; this side will not use it.
            //
            // #92: the *outcome* still says `unusable`, so the sweep can tell this
            // from a service that was never reached. Both are `Nothing` to the two
            // older callers and they are the opposite of each other to a retry --
            // this one reached the model and may have been billed for.
            return new CategorizerResult(CategorizerAnswer.Nothing, CategorizerOutcome.Unusable);
        }

        // Stored verbatim, neither trimmed nor lower-cased. Normalising here would
        // make this application the author of a value another process chose, and the
        // vocabulary is already closed at the end that owns it -- `Source` is a
        // StrEnum in contracts.py, so `Model` cannot leave that service. {Source}
        // verbatim in the log and bounded in the metric is the split #64's
        // cardinality trap forces: one more distinct string costs one more log line,
        // and one more metric series for ever.
        return new CategorizerResult(new CategorizerAnswer(category, source), CategorizerOutcome.Suggested);
    }

    /// <summary>Counts what was made of one answer, and logs the one worth reading.</summary>
    // The source is tagged on `suggested` and on nothing else, which is not a
    // shortcut. The tally it feeds is rendered beside the suggested count -- "8
    // suggested (rules=8)" -- so it answers "what produced the categories", and
    // counting declines in it would make that line report more producers than
    // categories.
    private void Record(CategorizerResult result, string kind, TimeSpan? elapsed)
    {
        var source = result.Outcome == CategorizerOutcome.Suggested ? result.Answer.Source : null;

        if (source is not null)
        {
            logger.LogInformation(
                "Categorizer {Kind} {Outcome}: {Category} by {Source} in {ElapsedMs:F0}ms.",
                kind, result.Outcome, result.Answer.Category, source, elapsed?.TotalMilliseconds ?? 0);
        }

        metrics.Record(result.Outcome, source, kind, elapsed);
    }

    /// <summary>One outcome per row, for a failure that happened to all of them at once.</summary>
    // The elapsed time is the *call's*, recorded once per row, and that is truthful
    // rather than convenient: every row in the batch waited exactly that long for an
    // answer it did not get. What it costs is that one slow batch of twenty puts
    // twenty identical samples into the histogram, so a percentile is weighted by how
    // many rows were owed rather than by how many calls were made -- which is the
    // right weighting when the question is "how long does a row wait".
    private void RecordForEachOf(
        IReadOnlyList<CategorizerBatchRow> rows, string outcome, string kind, TimeSpan? elapsed)
    {
        foreach (var _ in rows)
        {
            metrics.Record(outcome, source: null, kind, elapsed);
        }
    }
}

/// <summary>Drops the outcome for the caller that has no use for it.</summary>
// So that #92 changed neither of the signatures #39 and #67 settled on. The save
// path wants a suggestion or nothing and the preview path wants the three-state
// answer; handing either of them a word about retries would put the sweep's concern
// at a call site that has none.
internal static class CategorizerResultExtensions
{
    public static async Task<CategorizerAnswer> ContinueWithAnswer(this Task<CategorizerResult> result)
        => (await result).Answer;
}
