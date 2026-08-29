using System.Diagnostics.Metrics;
using LandMoney.Web.Categorizing;
using Microsoft.Extensions.DependencyInjection;

namespace LandMoney.Web.Tests.Categorizing;

/// <summary>The arithmetic behind the summary line: windows, percentiles, bounds.</summary>
// CategorizerClientTests asserts which outcome each branch records. This asserts
// what happens to the numbers afterwards -- which is worth separating, because
// these are the parts that are wrong quietly. A percentile computed the wrong way
// still prints a plausible number.
public class CategorizerMetricsTests
{
    private static CategorizerMetrics NewMetrics() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            TimeProvider.System);

    private static void RecordCalls(CategorizerMetrics metrics, string outcome, params double[] milliseconds)
    {
        foreach (var ms in milliseconds)
        {
            metrics.Record(outcome, source: null, TimeSpan.FromMilliseconds(ms));
        }
    }

    [Fact]
    public void An_untouched_window_is_nothing_to_report()
    {
        // What keeps an idle application silent. This service is used weekly by one
        // person; a heartbeat every minute would be almost everything it ever
        // writes to a log that costs money to keep.
        Assert.Null(NewMetrics().TakeWindow());
    }

    [Fact]
    public void Taking_a_window_resets_it()
    {
        // The property that makes each summary line a window rather than a running
        // total, which is what #35's --min-replicas 0 argues for: this process dies
        // after about fourteen idle minutes, so "since start" names a moment
        // nothing records.
        var metrics = NewMetrics();
        RecordCalls(metrics, CategorizerOutcome.Suggested, 10);

        var first = metrics.TakeWindow();

        Assert.NotNull(first);
        Assert.Equal(1, first.Calls);
        Assert.Null(metrics.TakeWindow());
    }

    [Fact]
    public void The_percentiles_are_durations_that_actually_happened()
    {
        // Nearest rank, so every number reported is one of the measurements rather
        // than an interpolation between two of them. Over 1..100ms that is 50 and
        // 95 exactly -- a test that also passes under interpolation would not say
        // which definition is in use, and the two disagree at the sample sizes this
        // sees.
        var metrics = NewMetrics();
        RecordCalls(metrics, CategorizerOutcome.Suggested, [.. Enumerable.Range(1, 100).Select(ms => (double)ms)]);

        var window = metrics.TakeWindow();

        Assert.NotNull(window);
        Assert.Equal(100, window.Measured);
        Assert.Equal(50, window.P50Ms);
        Assert.Equal(95, window.P95Ms);
        Assert.Equal(100, window.MaxMs);
    }

    [Fact]
    public void One_slow_call_among_fast_ones_is_visible_at_p95_and_invisible_in_the_middle()
    {
        // **#64's second trap, written as arithmetic.** Ten calls at 10ms and one
        // that hit the two-second connect timeout: the mean is 191ms, which
        // describes no call that was made and reads as "a bit slow". The p50 says
        // the ordinary case is 10ms and the p95 says something is taking two
        // seconds, which are the two facts worth having.
        var metrics = NewMetrics();
        RecordCalls(metrics, CategorizerOutcome.Suggested, [.. Enumerable.Repeat(10d, 10)]);
        RecordCalls(metrics, CategorizerOutcome.Timeout, 2000);

        var window = metrics.TakeWindow();

        Assert.NotNull(window);
        Assert.Equal(10, window.P50Ms);
        Assert.Equal(2000, window.P95Ms);
    }

    [Fact]
    public void A_call_that_was_never_made_is_counted_and_not_timed()
    {
        // The not-configured outcome, which is the only one with no duration. A
        // zero would be an instant success in the distribution, and enough of them
        // would put the p95 of a service that is not being called at all at zero
        // milliseconds -- a healthy-looking number for the least healthy state
        // there is.
        var metrics = NewMetrics();
        metrics.Record(CategorizerOutcome.NotConfigured, source: null, elapsed: null);

        var window = metrics.TakeWindow();

        Assert.NotNull(window);
        Assert.Equal(1, window.Calls);
        Assert.Equal(0, window.Measured);
        Assert.Equal(0, window.P95Ms);
    }

    [Theory]
    [InlineData("rules", "rules")]
    [InlineData("model", "model")]
    [InlineData("human", "human")]
    [InlineData("wizard", "other")]  // a producer this application has never heard of
    [InlineData("Rules", "other")]   // ordinal, like Categories.IsKnown, and for the same reason
    [InlineData("", "other")]
    public void A_tag_value_is_one_of_the_ones_this_application_declares(string source, string expected)
    {
        // #64's cardinality trap. A source is a string another process chooses, and
        // a dimension is for ever: one time series per distinct value, on a service
        // that could send a new one per request. The verbatim value survives in the
        // log line, which is where an unbounded string costs one line rather than
        // one series.
        Assert.Equal(expected, CategorizerMetrics.Label(source));
    }

    [Fact]
    public void A_window_larger_than_the_sample_says_how_much_it_missed()
    {
        // The cap exists so a runaway caller cannot turn a diagnostic into
        // unbounded memory, and it is 1024 against an application that saves once a
        // minute -- so this test is the only place it is ever reached. What is
        // asserted is not the number but that exceeding it is *reported*: a
        // percentile quietly computed over a prefix of the window is exactly the
        // kind of number that is believed for months.
        var metrics = NewMetrics();
        RecordCalls(metrics, CategorizerOutcome.Suggested, [.. Enumerable.Repeat(5d, 1025)]);

        var window = metrics.TakeWindow();

        Assert.NotNull(window);
        Assert.Equal(1025, window.Calls);
        Assert.Equal(1024, window.Measured);
        Assert.Equal(1, window.Dropped);
    }
}
