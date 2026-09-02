using System.Globalization;
using LandMoney.Web.Api;

namespace LandMoney.Web.Tests.Api;

/// <summary>Which days a month covers, now that the summary is a query. #95.</summary>
// #68 added a month up in the browser by comparing the first seven characters of a
// stored date, which was exact and could not be got wrong. Moving the sum to the
// server turns that string comparison into a range, and a range is a thing with two
// ends and a calendar behind it.
public class MonthRangeTests
{
    [Fact]
    public void A_month_is_the_days_from_its_first_up_to_the_next_ones()
    {
        Assert.True(MonthRange.TryParse("2026-08", out var first, out var next));

        Assert.Equal(new DateOnly(2026, 8, 1), first);
        Assert.Equal(new DateOnly(2026, 9, 1), next);
    }

    // Half-open is what makes the month's own length irrelevant, and February is
    // where the closed version -- `<= 2026-02-31`, or worse, `<= 2026-02-30` -- is
    // wrong in a way that costs rows rather than throwing.
    [Fact]
    public void February_needs_no_arithmetic_about_how_long_it_is()
    {
        Assert.True(MonthRange.TryParse("2024-02", out var first, out var next));

        Assert.Equal(new DateOnly(2024, 2, 1), first);
        Assert.Equal(new DateOnly(2024, 3, 1), next);
    }

    [Fact]
    public void December_rolls_into_the_next_year()
    {
        Assert.True(MonthRange.TryParse("2026-12", out var first, out var next));

        Assert.Equal(new DateOnly(2026, 12, 1), first);
        Assert.Equal(new DateOnly(2027, 1, 1), next);
    }

    // The upper bound is exclusive, said as a property rather than as three examples:
    // the first of the next month is never inside the month asked for. A closed range
    // counts one day twice, in two different months, and the number is plausible in
    // both.
    [Theory]
    [InlineData("2026-01")]
    [InlineData("2026-02")]
    [InlineData("2026-12")]
    public void The_first_of_the_next_month_is_outside_this_one(string month)
    {
        Assert.True(MonthRange.TryParse(month, out var first, out var next));

        Assert.Equal(first.AddMonths(1), next);
    }

    // --- what is refused ------------------------------------------------------

    // Each of these is a 400 rather than a total for some month the client did not
    // ask about. The unpadded "2026-8" is the one worth naming: it is what a client
    // that forgot padStart sends, and a parse that accepted it would answer with
    // August while the screen's heading said something else.
    //
    // **It is refused by TryParseExact and not by any check of ours**, which a
    // mutation established rather than a reading: deleting the length guard that used
    // to sit above the parse killed nothing here, because "MM" wants exactly two
    // digits. The guard is gone and this theory is what now holds the behaviour.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026-8")]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("08/2026")]
    [InlineData("2026-08-19")]
    [InlineData("last month")]
    public void Anything_that_is_not_a_month_is_refused(string? month)
    {
        Assert.False(MonthRange.TryParse(month, out _, out _));
    }

    // --- the culture rule, #31 ------------------------------------------------

    // The same rule TransactionCursor carries and for the same reason: nothing here
    // names a culture, so the machine supplies one, and under a non-Gregorian
    // calendar "2026-08" would be read as a Hijri year -- answering 200 with a total
    // for a month several centuries away from the one the client meant.
    //
    // Both cultures are here for two different halves of it. ro-RO renders and parses
    // "yyyy-MM" exactly as the invariant culture does, so it catches a separator
    // change and cannot catch a calendar change; ar-SA's default calendar is Umm
    // al-Qura.
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("ro-RO")]
    [InlineData("en-US")]
    public void A_month_means_the_same_days_whatever_culture_reads_it(string culture)
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.True(MonthRange.TryParse("2026-08", out var first, out var next));
            Assert.Equal(new DateOnly(2026, 8, 1), first);
            Assert.Equal(new DateOnly(2026, 9, 1), next);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
