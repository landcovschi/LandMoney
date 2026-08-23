namespace LandMoney.Web.Tests.Api;

/// <summary>A clock frozen at one instant, so a test can name the day it runs on.</summary>
// Written by hand rather than taken from Microsoft.Extensions.TimeProvider.Testing,
// the official package, whose FakeTimeProvider does this and more -- Advance(),
// SetUtcNow(), a settable LocalTimeZone. It lost on size: nothing here needs time
// to move, and this is six lines against a dependency that CLAUDE.md would want
// discussed first. It is the thing to reach for the day a test needs a clock that
// ticks: a cache expiry, a retry backoff, a token refresh.
//
// LocalTimeZone is settable here on purpose and is not decoration.
// PlausibleDateAttribute must read the UTC instant, and the cheapest way to prove
// it does is to hand it a clock whose local zone disagrees about what day it is.
internal sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? localTimeZone = null)
    : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;

    // TimeZoneInfo.Utc rather than the base class's TimeZoneInfo.Local, so a test
    // that does not care about zones still gets the same answer on every machine.
    public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone ?? TimeZoneInfo.Utc;

    /// <summary>Noon UTC on <paramref name="day"/>, far from either edge of the day.</summary>
    // Noon and not midnight: a clock pinned at 00:00 is one rounding away from the
    // previous day, and a test that fails for that reason would be blamed on the
    // rule it was written to check.
    public static FixedTimeProvider At(DateOnly day, TimeZoneInfo? localTimeZone = null) =>
        new(new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 00)), TimeSpan.Zero), localTimeZone);
}
