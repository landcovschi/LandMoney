using Microsoft.Extensions.Logging;

namespace LandMoney.Web.Tests.Categorizing;

/// <summary>One log entry, kept as its fields rather than as its sentence.</summary>
// The fields are the point. #64's whole argument is that a count is only readable
// if the same event carries the same field every time, and asserting on rendered
// prose would pin the wording instead -- so a reworded message, which is not a
// behaviour change, would turn a test red while a renamed field, which is, would
// not.
internal sealed record LogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Fields)
{
    public object? Field(string name) =>
        Fields.FirstOrDefault(field => field.Key == name).Value;
}

/// <summary>An ILogger that keeps what it was told.</summary>
// Written by hand for the same reason FixedTimeProvider is: the packaged answer
// (Microsoft.Extensions.Diagnostics.Testing, whose FakeLogger does this and more)
// is a dependency, and this is twenty lines. It is the thing to reach for the day
// a test needs scopes or log-level filtering, neither of which anything here has.
//
// Not thread-safe, deliberately. Every test using it starts and stops one hosted
// service and reads the list afterwards.
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(new LogEntry(
            logLevel,
            formatter(state, exception),
            // Every ILogger.Log call made through the LoggerExtensions helpers
            // passes a FormattedLogValues, which is this list of the template's
            // placeholders. The fallback is for a caller that logs a bare object,
            // which nothing here does.
            state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
}
