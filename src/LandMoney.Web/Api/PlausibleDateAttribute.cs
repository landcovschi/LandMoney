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
// What this gives up: DateTime.UtcNow is read inside, so the rule cannot be
// tested at a chosen date without moving the clock. TimeProvider is the modern
// answer, but DataAnnotations attributes are constructed by the runtime and
// cannot take an injected dependency -- reaching one means a service locator
// through validationContext.GetService, worth doing the day this gains a test
// and not before.
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

        // FromDateTime(DateTime.UtcNow) rather than DateTime.Today, and the
        // difference is not cosmetic: Today reads the machine's local zone. In a
        // Container Apps container that is UTC and on this machine it is not, so
        // the rule would agree with itself here and quietly shift by a day once
        // deployed -- passing every local test on the way.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
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
