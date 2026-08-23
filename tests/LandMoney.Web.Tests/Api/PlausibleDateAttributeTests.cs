using System.ComponentModel.DataAnnotations;
using System.Globalization;
using LandMoney.Web.Api;

namespace LandMoney.Web.Tests.Api;

/// <summary>Both bounds of the rule, and the boundary days themselves.</summary>
// Every test but the last one pins the clock, which is what #21 changed the
// attribute to allow. Testing relative to DateTime.UtcNow was the alternative and
// is recorded on the attribute: it needs no production change and it cannot ask
// what happens on a named day, which is where two of the tests below live.
//
// The date is arbitrary and fixed. It is deliberately not "today" computed at run
// time -- the whole point is that these numbers never move.
public class PlausibleDateAttributeTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    // The production pair, from CreateTransactionRequest: one day of slack ahead
    // to absorb every real UTC offset, five years behind to catch a mistyped year
    // in the other direction.
    private static PlausibleDateAttribute Rule(int daysAhead = 1, int yearsBehind = 5) =>
        new(daysAhead, yearsBehind);

    [Fact]
    public void Today_is_accepted() =>
        Assert.Null(Check(Today));

    [Fact]
    public void The_last_accepted_day_is_today_plus_the_allowance() =>
        Assert.Null(Check(Today.AddDays(1)));

    [Fact]
    public void One_day_past_the_allowance_is_refused()
    {
        var result = Check(Today.AddDays(2));

        Assert.NotNull(result);
        Assert.Equal("OccurredAt cannot be later than 2026-06-16.", result.ErrorMessage);
    }

    // The allowance is an argument, so zero has to mean what the summary says it
    // means: today is the last valid day.
    [Fact]
    public void An_allowance_of_zero_makes_today_the_last_valid_day()
    {
        Assert.Null(Check(Today, Rule(daysAhead: 0)));
        Assert.NotNull(Check(Today.AddDays(1), Rule(daysAhead: 0)));
    }

    [Fact]
    public void The_earliest_accepted_day_is_the_same_date_five_years_back() =>
        Assert.Null(Check(new DateOnly(2021, 6, 15)));

    [Fact]
    public void One_day_before_that_is_refused()
    {
        var result = Check(new DateOnly(2021, 6, 14));

        Assert.NotNull(result);
        Assert.Equal("OccurredAt cannot be earlier than 2021-06-15.", result.ErrorMessage);
    }

    // The near miss the five-year bound exists for: 2026 typed as 2016 is ten
    // years back, which a looser bound would wave through.
    [Fact]
    public void A_year_mistyped_by_a_decade_is_refused() =>
        Assert.NotNull(Check(new DateOnly(2016, 6, 15)));

    // Not a rule anyone chose -- it is DateOnly.AddYears clamping to the last day
    // of the target month, and it is written down here because a fixed clock is
    // the only thing that can ask. Five years before a leap day is 28 February,
    // so the boundary moves by one day in the four years out of five that are not
    // leap years, and the day before it is still refused.
    [Fact]
    public void Five_years_before_a_leap_day_lands_on_the_28th()
    {
        var leapDay = new DateOnly(2028, 2, 29);

        Assert.Null(Check(new DateOnly(2023, 2, 28), now: leapDay));
        Assert.NotNull(Check(new DateOnly(2023, 2, 27), now: leapDay));
    }

    // The trap the attribute's comment is about, made to fail rather than
    // described. The clock is at 23:00 UTC on the 15th while its local zone is
    // UTC+14, where it is already 13:00 on the 16th. With no allowance ahead, the
    // 16th is tomorrow and must be refused; anything reading the local time would
    // call it today and let it through.
    [Fact]
    public void The_clock_is_read_in_UTC_and_not_in_the_machines_zone()
    {
        var lateInTheUtcDay = new DateTimeOffset(2026, 6, 15, 23, 00, 00, TimeSpan.Zero);
        var fourteenHoursAhead = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+14 (test)", TimeSpan.FromHours(14), "UTC+14 (test)", "UTC+14 (test)");
        var clock = new FixedTimeProvider(lateInTheUtcDay, fourteenHoursAhead);

        Assert.Equal(new DateOnly(2026, 6, 16), DateOnly.FromDateTime(clock.GetLocalNow().DateTime));
        Assert.NotNull(Check(new DateOnly(2026, 6, 16), Rule(daysAhead: 0), clock));
    }

    [Fact]
    public void An_absent_date_is_left_to_Required() =>
        Assert.Null(Rule().GetValidationResult(null, ContextWith(FixedTimeProvider.At(Today))));

    // A DateTime is the interesting one: it is what this property used to be
    // before #17, so an attribute silently doing nothing is a live way for the
    // rule to disappear during a refactor. It cannot report that here -- a 400 for
    // a mistake no client made would be worse -- so the compiler is what has to
    // catch it, and this test only pins the behaviour so it is not mistaken for
    // coverage of it.
    [Theory]
    [InlineData("2026-06-15")]
    [InlineData(42)]
    public void A_value_that_is_not_a_DateOnly_is_not_this_attributes_problem(object value) =>
        Assert.Null(Rule().GetValidationResult(value, ContextWith(FixedTimeProvider.At(Today))));

    [Fact]
    public void A_value_that_is_a_DateTime_is_also_left_alone() =>
        Assert.Null(Rule().GetValidationResult(
            new DateTime(2026, 6, 15), ContextWith(FixedTimeProvider.At(Today))));

    [Fact]
    public void The_failure_names_the_member_it_came_from()
    {
        var result = Check(Today.AddDays(2));

        Assert.NotNull(result);
        Assert.Equal(["OccurredAt"], result.MemberNames);
    }

    [Fact]
    public void A_context_with_no_member_name_still_reports_the_failure()
    {
        var context = new ValidationContext(new object()) { DisplayName = "OccurredAt" };

        var result = Rule().GetValidationResult(Today.AddDays(400), context);

        Assert.NotNull(result);
        Assert.Empty(result.MemberNames);
    }

    // The fallback, and the reason it is not dead code: Validator.TryValidateObject
    // called without a service provider produces a ValidationContext whose
    // GetService answers null for everything. The attribute has to keep working
    // there, so this is the one test that reads the real clock -- and it stays
    // four hundred days from either boundary so that running it across midnight
    // cannot change the answer.
    [Fact]
    public void With_no_clock_registered_the_system_clock_is_used()
    {
        var context = ValidationContexts.WithNoServices("OccurredAt");
        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.Null(Rule().GetValidationResult(realToday.AddDays(-400), context));
        Assert.NotNull(Rule().GetValidationResult(realToday.AddDays(400), context));
    }

    // Found in review of #31, and the reason it is a [Theory] over two cultures
    // rather than one assertion: the interpolated {latest:yyyy-MM-dd} formatted
    // with the ambient culture, and for a date that does not merely choose
    // separators -- it chooses the calendar. Under ar-SA the same format string
    // produces a Hijri year, with no exception and nothing in a log, and the
    // React form would print that sentence under a date input showing
    // 2026-06-16.
    //
    // It cannot happen in production today: nothing sets a culture and there is
    // no request localization. It is a latent trap, and the mirror image of the
    // one CreateTransactionRequest already writes down for [Range], where
    // ParseLimitsInInvariantCulture exists for the same reason on the parsing
    // side -- one of them reads a limit, this one writes it back out.
    //
    // The culture is restored in a finally because xUnit hands test cases to
    // pooled threads, and a leaked CurrentCulture would surface as some
    // unrelated test failing for a reason that names nothing.
    [Theory]
    [InlineData("ar-SA")]   // a non-Gregorian calendar: the case that started this
    [InlineData("de-DE")]   // separators only, and still worth holding still
    public void The_message_names_the_date_the_same_way_in_every_culture(string culture)
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var tooLate = Check(Today.AddDays(2));
            var tooEarly = Check(new DateOnly(2021, 6, 14));

            Assert.Equal("OccurredAt cannot be later than 2026-06-16.", tooLate?.ErrorMessage);
            Assert.Equal("OccurredAt cannot be earlier than 2021-06-15.", tooEarly?.ErrorMessage);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static ValidationResult? Check(
        DateOnly occurredAt,
        PlausibleDateAttribute? rule = null,
        TimeProvider? clock = null,
        DateOnly? now = null) =>
        (rule ?? Rule()).GetValidationResult(
            occurredAt,
            ContextWith(clock ?? FixedTimeProvider.At(now ?? Today)));

    private static ValidationContext ContextWith(TimeProvider clock) =>
        ValidationContexts.ForMember("OccurredAt", clock);
}
