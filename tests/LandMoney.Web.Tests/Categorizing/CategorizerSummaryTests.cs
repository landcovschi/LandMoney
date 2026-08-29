using System.Diagnostics.Metrics;
using LandMoney.Web.Categorizing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LandMoney.Web.Tests.Categorizing;

/// <summary>The line #64 asks to be readable without grepping.</summary>
// Every test here runs the hosted service and stops it, and none of them waits for
// a tick. That is deliberate rather than a shortcut: the summary writes a final
// window on shutdown, so stopping it reaches the same rendering the timer reaches
// without a test that sleeps for an interval and hopes.
//
// What that leaves untested, said plainly the way #21 and #39 said it: that the
// timer fires on the configured interval at all. Driving a PeriodicTimer needs a
// clock whose CreateTimer is fake, which is Microsoft.Extensions.TimeProvider.Testing
// -- the package CLAUDE.md keeps out on the grounds that a frozen clock is six
// lines. The interval was checked by hand instead, against the running compose
// stack, and it is the one thing here a wrong value fails quietly at: a summary
// that never arrives looks exactly like an application that did nothing.
public class CategorizerSummaryTests
{
    private static CategorizerMetrics NewMetrics() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            TimeProvider.System);

    private static IConfiguration Interval(double seconds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Categorizer:SummaryIntervalSeconds"] = seconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

    /// <summary>Runs the service through one start and one stop, and returns what it logged.</summary>
    // An hour as the interval, so the only thing that can produce a summary is the
    // shutdown report. A short interval here would make the test a race between the
    // timer and StopAsync.
    //
    // **The wait is not a sleep, and the first version of this was wrong.**
    // BackgroundService.StartAsync does not run ExecuteAsync inline -- it is queued
    // to the thread pool -- so a start immediately followed by a stop cancels the
    // body before it has executed one statement, and every assertion here failed
    // with "the collection was empty". Waiting for the line the service writes when
    // it starts is a signal rather than a guess about how fast a runner is.
    private static async Task<List<LogEntry>> StartAndStop(
        CategorizerMetrics metrics, IConfiguration? configuration = null)
    {
        var logger = new RecordingLogger<CategorizerSummary>();
        var summary = new CategorizerSummary(
            metrics, configuration ?? Interval(3600), TimeProvider.System, logger);

        await summary.StartAsync(CancellationToken.None);

        // Both paths through ExecuteAsync write one line before doing anything else
        // -- the interval, or the reason there will be no summary -- so this is
        // reached whatever the configuration says. The deadline is generous because
        // it is only ever paid when something is broken.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (logger.Entries.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(logger.Entries.Count > 0, "The summary service never started.");

        await summary.StopAsync(CancellationToken.None);

        // BackgroundService.StopAsync waits for ExecuteAsync but does not observe
        // its exception -- it uses Task.WhenAny, which completes without rethrowing.
        // So a summary that threw would leave this list holding only the startup
        // line and the test would report a missing field, which reads as a rendering
        // bug and sends the reader to the wrong file. Awaiting the task surfaces the
        // real failure instead.
        if (summary.ExecuteTask is { } running)
        {
            await running;
        }

        return logger.Entries;
    }

    /// <summary>The one entry that is a summary, found by a field rather than by position.</summary>
    // `Calls` is on the summary line and on nothing else, so this does not depend on
    // how many other things the service decides to say.
    private static LogEntry Summary(IEnumerable<LogEntry> entries) =>
        Assert.Single(entries, entry => entry.Field("Calls") is not null);

    [Fact]
    public async Task A_stopped_categorizer_reads_as_three_timeouts_and_no_unreachables()
    {
        // **#64's first acceptance test, at the far end of the pipe.** The client
        // records the outcome (asserted in CategorizerClientTests); this asserts
        // that the number a person actually reads says the same thing. Three saves
        // against a categorizer that is not answering are three timeouts, and the
        // unreachable line stays at zero -- the two are one `null` on the wire and
        // must never be one number here.
        var metrics = NewMetrics();
        for (var i = 0; i < 3; i++)
        {
            metrics.Record(CategorizerOutcome.Timeout, source: null, TimeSpan.FromSeconds(2));
        }

        var summary = Summary(await StartAndStop(metrics));

        Assert.Equal(LogLevel.Information, summary.Level);
        Assert.Equal(3L, summary.Field("Timeout"));
        Assert.Equal(0L, summary.Field("Unreachable"));
        Assert.Equal(3L, summary.Field("Calls"));

        // p95 of three two-second calls is two seconds. The figure is what says the
        // failure is the clock rather than something fast and broken.
        Assert.Equal(2000d, summary.Field("P95Ms"));
    }

    [Fact]
    public async Task An_abstention_and_a_failure_are_two_different_fields()
    {
        // #64's third acceptance test. The whole line exists so that "it declined"
        // and "it did not answer" can be told apart by someone who was not there.
        var metrics = NewMetrics();
        metrics.Record(CategorizerOutcome.Abstained, source: null, TimeSpan.FromMilliseconds(30));
        metrics.Record(CategorizerOutcome.Unreachable, source: null, TimeSpan.FromMilliseconds(5));

        var summary = Summary(await StartAndStop(metrics));

        Assert.Equal(1L, summary.Field("Abstained"));
        Assert.Equal(1L, summary.Field("Unreachable"));
    }

    [Fact]
    public async Task The_suggestions_are_split_by_what_produced_them()
    {
        // Which is the number the issue is really after: "how often does the model
        // answer" is not answerable from a count of successes.
        var metrics = NewMetrics();
        metrics.Record(CategorizerOutcome.Suggested, "model", TimeSpan.FromMilliseconds(2100));
        metrics.Record(CategorizerOutcome.Suggested, "rules", TimeSpan.FromMilliseconds(4));

        var summary = Summary(await StartAndStop(metrics));

        Assert.Equal(2L, summary.Field("Suggested"));

        // One field rather than one per producer, because the set of producers is
        // decided by the other process and a message template's placeholders are
        // fixed when this compiles. Ordinal order, so the rendering does not depend
        // on the machine's culture.
        Assert.Equal("model=1, rules=1", summary.Field("BySource"));
    }

    [Fact]
    public async Task Nothing_happened_is_nothing_written()
    {
        // The rule that makes the line worth reading when it does appear.
        Assert.DoesNotContain(await StartAndStop(NewMetrics()), entry => entry.Field("Calls") is not null);
    }

    [Fact]
    public async Task A_non_positive_interval_turns_the_summary_off_and_says_so()
    {
        // A supported state rather than a mistake to guard against: anything
        // scraping the meter has no use for the same numbers again in prose. It says
        // so once, so that an absent summary is never a mystery -- and the absence
        // is total, including the shutdown report, because the service returns
        // before it ever starts a timer.
        var metrics = NewMetrics();
        metrics.Record(CategorizerOutcome.Suggested, "rules", TimeSpan.FromMilliseconds(9));

        var entries = await StartAndStop(metrics, Interval(0));

        var disabled = Assert.Single(entries);
        Assert.Equal(0d, disabled.Field("Seconds"));
        Assert.Null(disabled.Field("Calls"));
    }
}
