using LandMoney.Web.Data;
using LandMoney.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Categorizing;

/// <summary>Categorises the rows the save path no longer waits for. #92.</summary>
// **What this replaces.** Until #92 the create endpoint asked the categorizer
// before SaveChangesAsync and the user waited for the answer -- 142 ms with the
// rules behind the port, which is why nobody minded, and about 2.1 s per save
// once a model was there (#87). #59 had already bought the outage case down to
// 2 s with a separate connect timeout, and that bound is what made the inline
// call defensible; it stops being defensible when the *working* case is the slow
// one. So the row is written and answered for immediately, and this fills the
// category in afterwards.
//
// **A sweep over a column, rather than a queue in memory.** The alternative was
// a Channel<Guid> and a hosted service reading it, which is less code, needs no
// migration and answers in milliseconds. It loses on where this runs:
// --min-replicas 0 means the process dies after about fourteen idle minutes
// (#35) and again at every revision, so anything queued and not yet done goes
// with it, silently, with nothing recording that it was ever owed. That is the
// fourth time this project would have chosen a fallback whose absence nothing
// reports -- #39, #61, #62 and #64 are the others -- and the whole point of the
// column is that the owing survives the process. An external queue was the third
// option and lost on the same arithmetic that killed the Redis cache in #87: a
// managed queue is a monthly charge against an application one person uses
// weekly.
//
// What that costs, which #92's first trap asks to be written down: two writes
// where there was one, a second code path, and a window -- now seconds rather
// than milliseconds -- in which the API has answered 201 with a category the
// database does not have. The row is correct throughout that window; it is
// merely uncategorised, which is a state every part of this application has
// handled since #1 because a category has always been allowed to be absent.
public sealed class CategorizerSweep(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<CategorizerSweep> logger) : BackgroundService
{
    // Five seconds. The visible half of #92's acceptance test is "the category
    // appears without a page reload", and the client polls while anything on
    // screen is uncategorised, so this interval is most of what that wait feels
    // like. Short enough to read as "it arrived", long enough that an idle
    // application is one indexed query every five seconds against a table with
    // no rows to find.
    private const double DefaultIntervalSeconds = 5;

    // Twenty rows a tick. It bounds how long one tick can take against a model
    // at ~2.1 s a call, which matters because a tick that overruns the interval
    // simply starts late rather than in parallel -- PeriodicTimer does not queue.
    private const int DefaultBatchSize = 20;

    // The cap lives on PendingCategorization, because the response projection
    // needs the same number to tell a client whether a category is still coming.
    private const int DefaultMaxAttempts = PendingCategorization.DefaultMaxAttempts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Categorizer:SweepIntervalSeconds", DefaultIntervalSeconds);
        // Held inside what one request may carry -- see CategorizerBatch. A number over
        // the cap would be a 422 on every tick, which is indistinguishable in a log
        // from a categorizer that is misbehaving.
        var batchSize = CategorizerBatch.HeldWithinOneRequest(
            configuration.GetValue("Categorizer:SweepBatchSize", DefaultBatchSize));
        var maxAttempts = configuration.GetValue("Categorizer:MaxCategorizationAttempts", DefaultMaxAttempts);

        // Zero or negative turns the sweep off, and it is a supported state rather
        // than a mistake to guard against -- the same shape CategorizerSummary
        // uses for the same reason. What makes it safe is that nothing is lost by
        // it: rows keep their marker and are categorised whenever a sweep next
        // runs. It says so once, so that an application storing no categories is
        // never a mystery.
        if (seconds <= 0)
        {
            logger.LogInformation(
                "Categorizer:SweepIntervalSeconds is {Seconds}, so nothing will be categorised after the "
                + "fact. Rows are still marked as owing one and will be picked up if this is turned on.",
                seconds);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), time);

        // Once per process, and it earns its line the same two ways
        // CategorizerSummary's does: it is the only place the interval and the cap
        // in force are written down, and it is what a test waits for to know this
        // has started -- a BackgroundService's ExecuteAsync is queued to the thread
        // pool rather than run inline by StartAsync, so without something to wait
        // for, a test that starts and stops the service cancels the body before it
        // has executed one statement.
        logger.LogInformation(
            "Categorizing after the save, every {Seconds:F0}s, up to {BatchSize} rows a time, giving up on "
            + "a row after {MaxAttempts} attempts.",
            seconds, batchSize, maxAttempts);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnceAsync(batchSize, maxAttempts, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Anything still owed keeps its marker and is picked up by
            // whatever starts next, which is the entire reason the marker is a
            // column rather than a field on this object.
        }
    }

    /// <summary>One tick: claim a batch, ask about all of it, write what came back.</summary>
    internal async Task SweepOnceAsync(int batchSize, int maxAttempts, CancellationToken cancellationToken)
    {
        // A scope per tick. This class is a singleton -- AddHostedService registers
        // it as one -- and AppDbContext is scoped, so injecting it into the
        // constructor would capture one context for the lifetime of the process:
        // a change tracker that never empties and a connection that is never
        // returned. The C# parallel worth holding on to is that a BackgroundService
        // is not a request, so nothing creates or disposes a scope for it.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var categorizer = scope.ServiceProvider.GetRequiredService<CategorizerClient>();

        // **IgnoreQueryFilters, and it is the first call to it in this
        // repository.** AppDbContext's comment says as much and says why that
        // mattered: the global filter is chosen precisely so that there is nothing
        // to forget, and the exception has to be asked for by name so it shows up
        // in a diff.
        //
        // This is the case the exception exists for, and #92's third trap is
        // exactly it. The filter is `OwnerId == _currentUser.OwnerId`, and there
        // is no current user here -- a background service has no HttpContext, so
        // CurrentUser answers null, and `owner_id = NULL` is never true in SQL even
        // for a row whose owner_id is also null. Without this call the sweep would
        // select nothing, for ever, and look exactly like a categorizer that was
        // never reached. It is the mirror of #52's bug: there a null owner made one
        // person's rows visible to everyone, here it would make everyone's rows
        // visible to no one.
        //
        // What makes ignoring the filter safe rather than merely necessary is that
        // this operation genuinely has no owner. It reads a row, sends three fields
        // to a service that has no concept of accounts, and writes the answer back
        // to the same row -- nothing crosses between owners, and no data leaves the
        // process keyed to a person. The alternative was to register a mutable
        // ICurrentUser in the background scope and set it per row, which reads as
        // safer and is worse: it would make a fake signed-in user a supported
        // concept, one refactor away from being used somewhere it does not belong.
        // When retrieval starts sending the owner's own history as examples (#66's
        // last paragraph), that is the change that has to revisit this paragraph.
        var owed = await db.Transactions
            .IgnoreQueryFilters()
            .Where(PendingCategorization.Owed(maxAttempts))

            // Oldest first, so a backlog drains in the order it was created and a
            // row cannot be starved by newer ones arriving. CreatedAt rather than
            // OccurredAt: this is a queue, and its order is when the row was
            // written, not what day the money was spent.
            .OrderBy(transaction => transaction.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        // Nothing owed is the ordinary state of this application, and it is not an
        // event. Returning here rather than letting the client answer an empty batch
        // keeps that true on both sides: the Python endpoint refuses a request that
        // asks nothing, and a `refused` counted every five seconds would make the one
        // line that is supposed to mean something is wrong mean nothing at all.
        if (owed.Count == 0)
        {
            return;
        }

        // #93. One call for the whole batch, where #92 made one per row.
        //
        // The saving is not the round trips -- twenty requests to a service on the
        // same network cost a few milliseconds more than one. It is that the Python
        // side asks about the rows concurrently, so twenty model calls at about 2.1 s
        // each are about six seconds rather than forty-two. #62's imported rows are
        // the reason that matters at all: a three-hundred row file drains in a couple
        // of minutes instead of ten.
        var result = await categorizer.SweepCategoriesAsync(
            [.. owed.Select(transaction => new CategorizerBatchRow(
                Key(transaction), transaction.Description, transaction.Amount, transaction.Currency))],
            cancellationToken);

        // **The call failed as a whole, and only the oldest row is charged for it.**
        //
        // This is the one place #93 changes what #92 measured, and it is deliberately
        // written so that it does not. A batch failure says nothing about any
        // particular row in it -- the service is down, or slow, or refused the shape
        // of the request -- so charging all twenty would abandon a whole backlog after
        // two and a half minutes of outage, where #92's per-row loop cost one row in
        // the same time. Charging the oldest keeps that behaviour exactly: one call
        // per tick, one attempt per tick, and a backlog that survives an outage and
        // drains when the service comes back.
        //
        // It still terminates, which is the property the cap is actually for. The
        // batch is ordered oldest-first, so the row being charged is always the head
        // of the queue; a permanent failure retires it after `maxAttempts` ticks and
        // then starts on the next one. Nothing is retried for ever.
        //
        // What it gives up against CountsAgainstTheCap's own reasoning, said plainly:
        // a batch that timed out may have reached the model for every row in it and
        // been billed for all of them, and only one attempt is recorded. That
        // reasoning was written for a call that carried one row. The bill is bounded
        // by the row count and by the spend limit at Anthropic (#87), never by this
        // counter; what this counter bounds is an infinite retry, and it still does.
        if (result.CallFailure is { } failure)
        {
            if (CategorizerOutcome.CountsAgainstTheCap(failure))
            {
                await ChargeAsync(db, owed[0].Id, maxAttempts, cancellationToken);
            }

            return;
        }

        foreach (var transaction in owed)
        {
            // Every row that was sent has an entry, including the ones the service
            // did not answer for -- CategorizerClient fills those in as `unusable`
            // rather than leaving a hole, so there is no missing-key case to decide
            // about here. The lookup is still guarded, because a dictionary this
            // method did not build is not a dictionary this method may assume about.
            if (!result.Rows.TryGetValue(Key(transaction), out var row))
            {
                continue;
            }

            // The source is what says something answered at all -- #67's rule,
            // unchanged. A suggestion and an abstention both mean the question has
            // been put and answered, so the row stops owing one either way: the
            // rules decline on roughly a third of the labelled set and asking them
            // the same question again would buy the same word at the same price.
            if (row.Answer.Source is not null)
            {
                // The question is passed along with the answer, built from the entity
                // as it was read at the top of this tick -- see StoreAsync for what it
                // guards against. Built here rather than inside StoreAsync so that it
                // cannot accidentally be built from a fresher copy of the row, which
                // would make the guard compare a value against itself.
                await StoreAsync(
                    db,
                    transaction.Id,
                    CategorizerQuestion.About(transaction),
                    maxAttempts,
                    row.Answer.Suggestion,
                    cancellationToken);
                continue;
            }

            // The call worked and this row still got nothing usable out of it, so
            // whatever went wrong is about this row rather than about the service --
            // which is why the rest of the batch is still written rather than
            // abandoned, and why #92's `break` is gone from here. Charging the attempt
            // is what keeps the ceiling honest; see CountsAgainstTheCap for which
            // outcomes are charged and why a timeout is among them.
            if (CategorizerOutcome.CountsAgainstTheCap(row.Outcome))
            {
                await ChargeAsync(db, transaction.Id, maxAttempts, cancellationToken);
            }
        }
    }

    /// <summary>What the categorizer calls this row while it is being asked about.</summary>
    // The primary key, as a string, and written once so the two sides of the lookup
    // cannot spell it differently -- which would be a batch that answers nothing and
    // charges everything. "D" is Guid's default format and is named rather than
    // implied, because the key has to round-trip through JSON and a format chosen by
    // the default is a format that can change under a runtime upgrade.
    private static string Key(Transaction transaction) => transaction.Id.ToString("D");

    /// <summary>Writes the answer, and only if the row still wants one.</summary>
    // ExecuteUpdate rather than mutating the tracked entity and calling
    // SaveChanges, and this is the one decision in the file that is about
    // correctness rather than shape.
    //
    // The rows were read at the top of the tick and each call takes about two
    // seconds against a model, so a batch of twenty is the better part of a minute
    // during which somebody may correct a category on the screen. The entity held
    // in memory is a photograph taken before that happened, and SaveChanges would
    // write the prediction over the correction -- #92's second trap, arriving
    // through staleness rather than through a missing check. An in-memory
    // MayOverwrite would not catch it either: the stale copy says what the row said
    // a minute ago.
    //
    // Repeating the Owed predicate in the WHERE clause is what closes it. The guard
    // is evaluated by Postgres at the moment of the UPDATE, against the row as it
    // is then, so a row that has since been labelled by hand -- or has since
    // reached the cap -- matches nothing and no rows are written. This is the
    // caller CategorySources.MayOverwrite was written for in #63 and where it stops
    // being trivially true.
    //
    // **#94 added a second guard to the same clause, for the same reason one field
    // along.** A row can now be *edited* while this call is in flight, and an edit
    // that changes the description, the amount or the currency changes what the
    // question was -- so the answer coming back describes text that is no longer in
    // the row. Without the guard it would be stored anyway and would look entirely
    // plausible: a category, a source, and no way to tell it was computed from a
    // typo the person has since fixed.
    //
    // What happens instead is the good failure. The UPDATE matches nothing, so the
    // row keeps the marker the edit put back on it, and the next tick asks the
    // question that is actually on the screen. Nothing has to be co-ordinated and
    // nothing is lost except one call.
    //
    // **ChargeAsync deliberately does not take the same guard**, and the asymmetry
    // is the point rather than an oversight. This one is about a fact -- the answer
    // is about the wrong text, so it is worthless. That one is about a bill: the
    // call was made and, against a model, paid for, whether or not the row moved
    // underneath it. Guarding the charge as well would also make repeated editing a
    // way to never exhaust the cap, which is precisely the unbounded retry the cap
    // exists to stop.
    private static Task StoreAsync(
        AppDbContext db,
        Guid id,
        CategorizerQuestion asked,
        int maxAttempts,
        CategorySuggestion? suggestion,
        CancellationToken cancellationToken)
    {
        // Unpacked into two locals before the expression tree rather than written
        // as `suggestion == null ? null : suggestion.Category` inside SetProperty.
        // The value handed to SetProperty is part of a tree EF has to make sense
        // of, and a captured local is a parameter it cannot misread; a conditional
        // over a captured object is one more thing for the translator to have an
        // opinion about, for no gain. It also keeps #59's invariant visible in one
        // line -- both come from one nullable value, so they cannot disagree.
        var category = suggestion?.Category;
        var source = suggestion?.Source;

        return db.Transactions
            .IgnoreQueryFilters()
            .Where(transaction => transaction.Id == id)
            .Where(PendingCategorization.Owed(maxAttempts))
            .Where(asked.Unchanged())
            .ExecuteUpdateAsync(
                setters => setters

                    // Both columns from one nullable value, which is #59's
                    // invariant: a source exists exactly when a category does. An
                    // abstention arrives here as a null suggestion and writes null
                    // to both, which is what they already were -- so an abstention
                    // is recorded by the row ceasing to owe anything, not by a
                    // value.
                    .SetProperty(transaction => transaction.Category, category)
                    .SetProperty(transaction => transaction.CategorySource, source)

                    // Nothing is owed any more. Null rather than a final count,
                    // because the count is only interesting while something is
                    // still owed, and leaving one behind would make an answered row
                    // indistinguishable from an abandoned one.
                    .SetProperty(transaction => transaction.CategorizationAttempts, (int?)null),
                cancellationToken);
    }

    /// <summary>Records that an attempt was made and produced nothing usable.</summary>
    // Guarded by the same predicate and for the same reason: a row somebody
    // labelled while this call was in flight owes nothing, so it must not have an
    // attempt charged against it either.
    //
    // The increment reads the column inside the UPDATE rather than sending a number
    // computed here, so two sweeps overlapping -- which only happens across a
    // revision, since the app runs at --max-replicas 1 -- add up instead of one
    // overwriting the other with a stale value.
    private static Task ChargeAsync(
        AppDbContext db,
        Guid id,
        int maxAttempts,
        CancellationToken cancellationToken)
        => db.Transactions
            .IgnoreQueryFilters()
            .Where(transaction => transaction.Id == id)
            .Where(PendingCategorization.Owed(maxAttempts))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    transaction => transaction.CategorizationAttempts,
                    transaction => transaction.CategorizationAttempts + 1),
                cancellationToken);
}
