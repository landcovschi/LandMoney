using System.ComponentModel.DataAnnotations;

namespace LandMoney.Web.Api;

/// <summary>
/// Refuses a <see cref="DateOnly"/> that a person could not plausibly be entering:
/// more than <see cref="MaxDaysAhead"/> days past today in UTC, or more than
/// <see cref="MaxYearsBehind"/> years before it.
/// </summary>
// Renamed from NotFarInFutureAttribute when the past bound was added in review.
// The old name described one half of the rule, and a class that checks both while
// claiming one is the kind of thing that is trusted right up until it is read.
//
// One attribute with two bounds rather than two attributes with one each: this is
// a single question -- is this a date a human is plausibly typing today -- and
// splitting it would put the two halves of one answer in two files, each with
// half the reasoning. The cost is that neither bound can be applied without the
// other, which nothing here wants to do.
//
// The clock is a TimeProvider found through validationContext.GetService. That
// is a service locator, and it is the only way an attribute can take a
// dependency at all: DataAnnotations attributes are constructed by the runtime
// out of the arguments in their brackets, so there is no constructor to inject
// into. The previous version of this comment said this was "worth doing the day
// this gains a test"; #21 is that day.
//
// What lost: testing relative to DateTime.UtcNow, needing no production change.
// It fails at two things. A test that computes today the same way the attribute
// does is asserting that two copies of one expression agree -- it would keep
// passing if this switched to DateTime.Today, which is the exact mistake the
// comment inside IsValid exists to prevent, and which local time hides on this
// machine for all but a few hours a day. And it cannot ask what happens on a
// chosen date, so the leap-day clamp in DateOnly.AddYears has nowhere to be
// written down.
//
// The fallback to TimeProvider.System is what keeps the attribute usable from a
// bare Validator.TryValidateObject, where the ValidationContext carries no
// service provider. ValidationFilter<T> passes the request's, and Program.cs
// registers TimeProvider.System into it, so the two paths agree.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class PlausibleDateAttribute : ValidationAttribute
{
    public PlausibleDateAttribute(int maxDaysAhead, int maxYearsBehind)
    {
        MaxDaysAhead = maxDaysAhead;
        MaxYearsBehind = maxYearsBehind;
    }

    /// <summary>Days past today in UTC that are still accepted. Zero means today is the last valid day.</summary>
    public int MaxDaysAhead { get; }

    /// <summary>Years before today that are still accepted.</summary>
    public int MaxYearsBehind { get; }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        // Absence is [Required]'s question, not this attribute's. Every
        // DataAnnotations attribute except Required is built this way, and
        // breaking the convention would mean an optional date could never be
        // left empty -- the two rules would be impossible to combine.
        if (value is null)
        {
            return ValidationResult.Success;
        }

        // Not a DateOnly means the attribute is on the wrong property: a
        // programming mistake, not something a user typed. Failing here would
        // report a bug in the model as though the request were at fault, and
        // would send a 400 for something no client can fix.
        if (value is not DateOnly occurredAt)
        {
            return ValidationResult.Success;
        }

        // GetService returns null when nothing is registered and when the
        // ValidationContext was built without a service provider at all, which
        // is what `as` plus the fallback is for. A cast would throw on the first
        // and a null-reference on the second.
        var clock = validationContext.GetService(typeof(TimeProvider)) as TimeProvider
            ?? TimeProvider.System;

        // GetUtcNow().UtcDateTime rather than GetLocalNow() or DateTime.Today,
        // and the difference is not cosmetic: both of those read a local zone.
        // In a Container Apps container that is UTC and on this machine it is
        // not, so the rule would agree with itself here and quietly shift by a
        // day once deployed -- passing every local test on the way. TimeProvider
        // does not remove that trap, it renames it, which is why one test pins a
        // clock whose LocalTimeZone is UTC+14 at an instant late in the UTC day:
        // reaching for the local time there lands on tomorrow and the test says
        // so.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var latest = today.AddDays(MaxDaysAhead);
        var earliest = today.AddYears(-MaxYearsBehind);

        if (occurredAt > latest)
        {
            return Failure(
                $"{validationContext.DisplayName} cannot be later than {latest:yyyy-MM-dd}.",
                validationContext);
        }

        if (occurredAt < earliest)
        {
            return Failure(
                $"{validationContext.DisplayName} cannot be earlier than {earliest:yyyy-MM-dd}.",
                validationContext);
        }

        return ValidationResult.Success;
    }

    // Passing the member name is what files the message under "occurredAt" in the
    // 400 body instead of under the empty key. That is where a form looks to put
    // the message beside the field it belongs to; without it every error arrives
    // as an unattributed sentence.
    private static ValidationResult Failure(string message, ValidationContext validationContext) =>
        new(message, validationContext.MemberName is null ? [] : [validationContext.MemberName]);
}
