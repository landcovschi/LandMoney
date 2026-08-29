namespace LandMoney.Web.Categorizing;

/// <summary>Writes one line saying what the categorizer did, whenever it did anything.</summary>
// #64 asks for the counts to be readable without grepping, and defers a metrics
// endpoint to its own issue. This is what stands in the gap: the numbers
// CategorizerMetrics holds, rendered once per window into the one place both a
// developer machine and a Container App already look.
//
// **It is silent when nothing happened.** CategorizerMetrics.TakeWindow answers
// null for an empty window, so an idle application writes nothing at all -- which
// matters more here than it sounds. This application is used weekly by one person,
// so a heartbeat every minute would be almost the whole of what it ever logs, and
// a log nobody reads is not observability.
//
// What lost, and it is the obvious alternative: totals since the process started,
// re-logged each time. It reads better on a long-lived server and it is the wrong
// shape for this one -- with --min-replicas 0 the process dies after about
// fourteen idle minutes (#35), so "since start" means "since some point this
// afternoon that nothing records".
public sealed class CategorizerSummary(
    CategorizerMetrics metrics,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<CategorizerSummary> logger) : BackgroundService
{
    // A minute. Short enough that the acceptance test in #64 -- stop the
    // categorizer, save three transactions, read the numbers -- is a wait rather
    // than a coffee break, and long enough that a busy afternoon is sixty lines
    // instead of one per save. Configuration rather than a constant so it can be
    // lengthened in production without a deployment of code.
    private const double DefaultIntervalSeconds = 60;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Categorizer:SummaryIntervalSeconds", DefaultIntervalSeconds);

        // Zero or negative turns the summary off, and it is a supported state
        // rather than a mistake to guard against: the per-call log lines are the
        // durable record, and someone shipping this behind a metrics scrape has no
        // use for a second copy of the same numbers in prose. It says so once, so
        // that an absent summary is never a mystery.
        if (seconds <= 0)
        {
            logger.LogInformation(
                "Categorizer:SummaryIntervalSeconds is {Seconds}, so no summary line will be written. "
                + "The per-call lines are unaffected.",
                seconds);
            return;
        }

        // The TimeProvider overload, so a test drives this with a fake clock rather
        // than with a Task.Delay and a hope. The same registration PlausibleDateAttribute
        // uses (#21) -- Program.cs registers TimeProvider.System, which the framework
        // does not do by itself.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), time);

        // Once per process, and it earns its line twice over. It is the only place
        // the interval in force is written down -- a summary that arrives every ten
        // minutes because somebody set the wrong number otherwise looks like an
        // application that is barely used -- and it is what a test waits for to know
        // this has started, because a BackgroundService's ExecuteAsync is queued to
        // the thread pool rather than run inline by StartAsync. Measured, after four
        // tests failed with an empty log and a cancelled task: with no wait at all,
        // StopAsync cancels before the body has run a single statement.
        logger.LogInformation(
            "Categorizer summary every {Seconds:F0}s, and only when something happened.", seconds);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Report();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Nothing has gone wrong, and there is one last thing worth
            // saying -- see below.
        }

        // The final window, which is the one most likely to be lost otherwise. A
        // replica that scales to zero is stopped between ticks by definition, so
        // without this the last few saves of a session are counted and thrown away.
        // Hosted services are stopped before the logging providers are disposed, so
        // this line does reach the console.
        Report();
    }

    private void Report()
    {
        if (metrics.TakeWindow() is not { } window)
        {
            return;
        }

        // Every count is its own placeholder rather than one rendered blob, because
        // under the JSON console formatter each becomes a field: "how often was it
        // unreachable last week" is then a query over a number, which is what #64
        // means by readable without grepping. The prose is for the person reading
        // `docker compose logs`.
        logger.LogInformation(
            // "{Calls} recorded" rather than "{Calls} calls", and "of them" rather
            // than "calls" below, because a window of one is common on a service
            // used weekly and "1 calls" is the sort of thing that makes a reader
            // trust a number less than it deserves.
            "Categorizer over the last {WindowSeconds:F0}s: {Calls} recorded ({ByKind}) -- "
            + "{Suggested} suggested ({BySource}), {Abstained} abstained, {Refused} refused, "
            + "{Timeout} timed out, {Unreachable} unreachable, {Unreadable} unreadable, "
            + "{Unusable} unusable, {NotConfigured} with no categorizer configured, "
            + "{Abandoned} abandoned by the caller. "
            + "Latency over {Measured} of them: p50 {P50Ms:F0}ms, p95 {P95Ms:F0}ms, max {MaxMs:F0}ms.",
            window.Length.TotalSeconds,
            window.Calls,
            ByKind(window),
            Count(window, CategorizerOutcome.Suggested),
            BySource(window),
            Count(window, CategorizerOutcome.Abstained),
            Count(window, CategorizerOutcome.Refused),
            Count(window, CategorizerOutcome.Timeout),
            Count(window, CategorizerOutcome.Unreachable),
            Count(window, CategorizerOutcome.Unreadable),
            Count(window, CategorizerOutcome.Unusable),
            Count(window, CategorizerOutcome.NotConfigured),
            Count(window, CategorizerOutcome.Abandoned),
            window.Measured,
            window.P50Ms,
            window.P95Ms,
            window.MaxMs);

        // Its own line, and only when it happens, so the sentence above keeps one
        // shape. It means the percentiles describe the first thousand calls of the
        // window rather than all of them, which is a caveat on a number and not an
        // error -- but a percentile quietly computed over a sample is exactly the
        // kind of thing that is believed for months.
        if (window.Dropped > 0)
        {
            logger.LogWarning(
                "{Dropped} calls in that window were not timed, so the percentiles above cover "
                + "{Measured} of {Calls}.",
                window.Dropped, window.Measured, window.Calls);
        }
    }

    private static long Count(CategorizerWindow window, string outcome) =>
        window.ByOutcome.GetValueOrDefault(outcome);

    /// <summary>What asked for those calls, as one field. #67.</summary>
    // Every kind is printed, including the ones that did not happen, which is the
    // opposite of what BySource below does. The reason for the difference is who
    // owns the vocabulary: there are exactly two kinds and this application chose
    // both, so "preview=0" is a fact worth reading -- it says the screen asked for
    // nothing, which after #67 shipped is a symptom. A source that did not answer
    // is not a fact about anything, because the set of producers belongs to the
    // other process.
    private static string ByKind(CategorizerWindow window) =>
        string.Join(
            ", ",
            CategorizerKind.All.Select(kind => $"{kind}={window.ByKind.GetValueOrDefault(kind)}"));

    /// <summary>The suggested calls split by who produced them, as one field.</summary>
    // A string rather than a placeholder per source, because the set is decided by
    // the other process and a message template's placeholders are fixed at compile
    // time. The queryable version of this is the `source` tag on the metric and the
    // {Source} field on each per-call line; this is the version a person reads.
    private static string BySource(CategorizerWindow window) =>
        window.BySource.Count == 0
            ? "none"
            : string.Join(
                ", ",
                window.BySource
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
}
