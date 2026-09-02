using System.Globalization;

namespace LandMoney.Web.Api;

/// <summary>One calendar month, as the half-open range of days it covers. #95.</summary>
// The summary moved to the server because a paged list cannot be summed on the
// client -- #95's third trap says so in as many words, and #68's own text predicted
// the day: "it stops being fine silently ... the fix on that day is a sum on the
// server in decimal, not a bigger page". This is the half of that which decides
// *which* rows.
//
// **The month is the client's and not the server's**, which is the one thing about
// this that is not obvious. OccurredAt is a plain day with no zone (#17), so "this
// month" means the month it is where the reader is; a server picking it off its own
// clock would put the first and the last day of every month in the wrong bucket for
// most of the world. So it arrives as a parameter, and monthOf() in the client reads
// the local clock to produce it -- the same reasoning #68 wrote down, moved across
// the wire rather than changed.
public static class MonthRange
{
    /// <summary>The one shape a month may be written in: "2026-08".</summary>
    private const string Format = "yyyy-MM";

    /// <summary>Reads "2026-08" into the days [first, next), or answers false.</summary>
    // Half-open, and it is the only comparison shape that is right for a date column
    // without knowing the month's length: `>= 2026-08-01 AND < 2026-09-01` needs no
    // arithmetic about 28, 30 or 31 and cannot be got wrong in February. A closed
    // `<= 2026-08-31` is the version somebody writes by hand, and it is a row lost on
    // the last day of every month it gets wrong.
    //
    // It is also what keeps the range usable by the index. Both bounds are on
    // occurred_at, which is the second column of
    // ix_transactions_owner_id_occurred_at_created_at with owner_id pinned by
    // equality above it -- so this is a range scan rather than a table read.
    //
    // ParseExact against InvariantCulture, which is #31's rule and matters here for
    // the reason a permissive parse always matters: TryParse against the current
    // culture accepts "08/2026", and under a non-Gregorian calendar it would read a
    // Hijri year out of digits a client wrote as Gregorian. Both would answer 200
    // with a total for the wrong month.
    public static bool TryParse(string? month, out DateOnly first, out DateOnly next)
    {
        first = default;
        next = default;

        // Length is checked before the parse rather than left to it, because
        // "yyyy-MM" is a *minimum* width pattern: DateTime.TryParseExact accepts a
        // one-digit month against "MM" in some cultures, so "2026-8" would parse and
        // then be echoed back in a shape the client never sent.
        if (month is not { Length: 7 })
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                month, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        first = new DateOnly(parsed.Year, parsed.Month, 1);
        next = first.AddMonths(1);

        return true;
    }
}
