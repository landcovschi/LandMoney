using System.Diagnostics;          // TagList -- not in System.Diagnostics.Metrics, although every user of it is
using System.Diagnostics.Metrics;
using LandMoney.Web.Api;           // CategorySources, for the bounded tag values

namespace LandMoney.Web.Categorizing;

/// <summary>What one summary line reports: the window since the previous one.</summary>
// Deltas rather than totals since the process started, and that is a decision the
// deployment makes rather than a preference. With --min-replicas 0 (#35) this
// process dies after roughly fourteen idle minutes, so a running total describes
// one replica's life and nothing more; a window describes what happened between
// two points in time, which is a thing worth reading in a log and a thing that
// still adds up correctly across replicas afterwards.
public sealed record CategorizerWindow(
    TimeSpan Length,
    long Calls,
    IReadOnlyDictionary<string, long> ByOutcome,
    // #67. What asked -- a save, or a description being typed. It is its own
    // dimension rather than a second set of counters because a preview fails in
    // exactly the nine ways a save does; what differs is only whether anything
    // came of it, and how many there are.
    //
    // What this deliberately does not do: split the outcomes by kind. That would
    // answer "did saves get categories" without reading a log line, and it costs a
    // dictionary of dictionaries and a summary line that no longer has one shape.
    // The per-call lines carry both words, so the question is a query rather than
    // a number, and the number that could not be recovered from anywhere else --
    // how many calls each path made, which against the model is the bill -- is
    // here.
    IReadOnlyDictionary<string, long> ByKind,
    IReadOnlyDictionary<string, long> BySource,
    int Measured,
    int Dropped,
    double P50Ms,
    double P95Ms,
    double MaxMs);

/// <summary>Counts what the categorizer did, per outcome, and how long it took.</summary>
/// <remarks>
/// Registered as a singleton and written to from every exit path of
/// <see cref="CategorizerClient"/>.
/// </remarks>
// #64. Two consumers of one recording path, which is the shape that keeps the
// deferred half of that issue cheap:
//
//   * a System.Diagnostics.Metrics Meter, which nothing reads today. A metrics
//     endpoint -- the second step #64 explicitly does not ask for -- is then an
//     OpenTelemetry package and a line in Program.cs, attaching a second listener
//     to these same instruments with no change to any call site.
//   * an in-process tally that CategorizerSummary reads and writes to the log,
//     which is what makes the numbers readable today, on a machine that has no
//     Prometheus and in a container app that has no metrics scrape.
//
// **The log is the durable record and the tally is a convenience.** Everything
// below dies with the process, and with --min-replicas 0 that is every quarter of
// an hour; the per-call line and the summary line both survive in Log Analytics.
// So the question "what did the categorizer do last Tuesday" is answered by a
// query over log fields, and the tally exists so that the same question about the
// last minute is answered by reading one line.
//
// A lock rather than Interlocked, and it is not laziness. A save is a rare event
// -- one person, weekly -- so contention here is theoretical, while a lock is what
// makes "take the window and reset it" a single atomic step. Counting with
// Interlocked and resetting without one is how a summary loses the call that
// arrived while it was being written.
public sealed class CategorizerMetrics
{
    /// <summary>The meter name a listener subscribes to.</summary>
    public const string MeterName = "LandMoney.Categorizer";

    // What a tag value becomes when the answer names a producer this application
    // does not recognise. #64's first trap is about the description, and this is
    // the same trap one field along: `source` is a string chosen by another
    // process, so tagging it verbatim would let a misbehaving service mint a new
    // time series per request. The log line keeps the value it actually sent --
    // see CategorizerClient -- so nothing is lost, it is only kept out of the
    // dimension.
    private const string OtherSource = "other";

    // Enough that this application will never reach it: one save a minute against a
    // sixty-second window is sixty. It exists so that a runaway caller cannot turn
    // a diagnostic into unbounded memory, and when it does bite the summary says
    // so rather than quietly reporting a percentile over the first thousand calls
    // of a window as though it covered all of them.
    private const int MaxDurations = 1024;

    private readonly Counter<long> _calls;
    private readonly Histogram<double> _duration;
    private readonly TimeProvider _time;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _byOutcome = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _byKind = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _bySource = new(StringComparer.Ordinal);
    private readonly List<double> _durations = new(capacity: 64);
    private int _dropped;
    private long _calledSinceWindow;
    private long _windowStartedAt;

    public CategorizerMetrics(IMeterFactory meterFactory, TimeProvider time)
    {
        _time = time;
        _windowStartedAt = time.GetTimestamp();

        // IMeterFactory rather than `new Meter(...)`: the factory owns the Meter's
        // lifetime and scopes it to the container, which is what lets a test build
        // its own and not share counters with another test running beside it.
        var meter = meterFactory.Create(MeterName);

        // Names are dotted and lower-case, which is the OpenTelemetry convention
        // rather than the .NET one -- these are read by whatever scrapes them, not
        // by C#. The unit strings are UCUM, where a count of things is written in
        // braces.
        _calls = meter.CreateCounter<long>(
            "landmoney.categorizer.calls",
            unit: "{call}",
            description: "Calls to the categorizer, by outcome.");

        // Seconds, because that is what the convention says a duration is, even
        // though every log line here reports milliseconds because that is what a
        // person reads. A histogram rather than a mean: #64's second trap is that a
        // mean hides the failure this exists to find, and a two-second connect
        // timeout among fast answers is invisible in an average and obvious at p95.
        _duration = meter.CreateHistogram<double>(
            "landmoney.categorizer.duration",
            unit: "s",
            description: "How long a call to the categorizer took, including the ones that failed.");
    }

    /// <summary>One call, one outcome.</summary>
    /// <param name="outcome">A value from <see cref="CategorizerOutcome"/>, never free text.</param>
    /// <param name="source">Who produced the category, when there is one. Bounded before it is tagged.</param>
    /// <param name="kind">A value from <see cref="CategorizerKind"/>: what asked for this.</param>
    /// <param name="elapsed">
    /// How long the call took, or null when there was no call -- which is only
    /// <see cref="CategorizerOutcome.NotConfigured"/>. Recording a zero there would
    /// put a heap of instant successes into the histogram and drag every percentile
    /// down, so the absence is represented rather than approximated.
    /// </param>
    public void Record(string outcome, string? source, string kind, TimeSpan? elapsed)
    {
        var label = source is null ? null : Label(source);

        // `outcome` always; `source` only when there is one, so the counter's
        // suggested line splits by producer -- #64 asks for exactly that -- while
        // every other outcome keeps one series instead of one per producer that did
        // not answer.
        // `kind` on both instruments and always. Unlike `source` it is never
        // absent -- something always asked -- and unlike `source` it is this
        // application's own word, so there is no bounding to do: CategorizerKind
        // has two members and nothing else can reach here.
        var tags = new TagList { { "outcome", outcome }, { "kind", kind } };
        if (label is not null)
        {
            tags.Add("source", label);
        }

        _calls.Add(1, tags);

        if (elapsed is { } took)
        {
            _duration.Record(
                took.TotalSeconds, new TagList { { "outcome", outcome }, { "kind", kind } });
        }

        lock (_gate)
        {
            _calledSinceWindow++;
            _byOutcome[outcome] = _byOutcome.GetValueOrDefault(outcome) + 1;
            _byKind[kind] = _byKind.GetValueOrDefault(kind) + 1;

            if (label is not null)
            {
                _bySource[label] = _bySource.GetValueOrDefault(label) + 1;
            }

            if (elapsed is not { } measured)
            {
                return;
            }

            if (_durations.Count < MaxDurations)
            {
                _durations.Add(measured.TotalMilliseconds);
            }
            else
            {
                _dropped++;
            }
        }
    }

    /// <summary>The window since the last take, or null if nothing happened in it.</summary>
    /// <remarks>Taking a window resets it.</remarks>
    // Null rather than a window of zeros, and that is what keeps the summary quiet.
    // A line every minute saying nothing happened is a line nobody reads, on a
    // service used weekly -- and it would be the majority of what this application
    // writes to a log that costs money to keep.
    public CategorizerWindow? TakeWindow()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var length = _time.GetElapsedTime(_windowStartedAt, now);

            if (_calledSinceWindow == 0)
            {
                // The clock still moves. Otherwise the first window after a quiet
                // spell would claim to cover the whole idle period, and its rate
                // would be read as one call an hour.
                _windowStartedAt = now;
                return null;
            }

            _durations.Sort();

            var window = new CategorizerWindow(
                length,
                _calledSinceWindow,
                new Dictionary<string, long>(_byOutcome, StringComparer.Ordinal),
                new Dictionary<string, long>(_byKind, StringComparer.Ordinal),
                new Dictionary<string, long>(_bySource, StringComparer.Ordinal),
                Measured: _durations.Count,
                Dropped: _dropped,
                P50Ms: Percentile(_durations, 0.50),
                P95Ms: Percentile(_durations, 0.95),
                MaxMs: _durations.Count == 0 ? 0 : _durations[^1]);

            _byOutcome.Clear();
            _byKind.Clear();
            _bySource.Clear();
            _durations.Clear();
            _dropped = 0;
            _calledSinceWindow = 0;
            _windowStartedAt = now;

            return window;
        }
    }

    /// <summary>The bounded tag value for a source this application did not choose.</summary>
    // Exposed for the test that pins the bound. Ordinal and case-sensitive, the
    // same treatment `Categories.IsKnown` gives a category and for the same reason:
    // "Rules" is not a spelling to be forgiving about, it is evidence that
    // something is sending a value it did not get from the categorizer.
    public static string Label(string source) =>
        source is CategorySources.Rules or CategorySources.Model or CategorySources.Human
            ? source
            : OtherSource;

    /// <summary>Nearest-rank percentile over a sorted list, in milliseconds.</summary>
    // The simple definition -- the value at ceil(p x n) -- so every number a summary
    // reports is a duration that actually happened rather than an interpolation
    // between two that did. At the sample sizes this sees, an interpolated p95 over
    // seven calls would carry more precision than meaning.
    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
