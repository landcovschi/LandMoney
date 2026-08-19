using System.ComponentModel.DataAnnotations;

namespace LandMoney.Web.Api;

/// <summary>
/// Refuses a <see cref="DateOnly"/> more than <see cref="MaxDaysAhead"/> days past
/// today in UTC. Money that has not been spent yet is not a transaction.
/// </summary>
// A custom ValidationAttribute rather than an `if` in the handler, for one
// reason: it is declarative, so the rule sits on the field it governs and joins
// the same 400 ValidationProblem as every other rule, in the same shape. An `if`
// in the handler has to build that response by hand and drifts out of step the
// first time a second endpoint accepts a date.
//
// What this gives up: DateTime.UtcNow is read inside, so the rule cannot be
// tested at a chosen date without moving the clock. TimeProvider is the modern
// answer, but DataAnnotations attributes are constructed by the runtime and
// cannot take an injected dependency -- reaching one means a service locator
// through validationContext.GetService, worth doing the day this gains a test
// and not before.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class NotFarInFutureAttribute : ValidationAttribute
{
    public NotFarInFutureAttribute(int maxDaysAhead) => MaxDaysAhead = maxDaysAhead;

    /// <summary>Days past today in UTC that are still accepted. Zero means today is the last valid day.</summary>
    public int MaxDaysAhead { get; }

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
        var latestAllowed = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(MaxDaysAhead);

        if (occurredAt <= latestAllowed)
        {
            return ValidationResult.Success;
        }

        // Passing the member name is what files the message under "occurredAt"
        // in the 400 body instead of under the empty key. That is where a form
        // looks to put the message beside the field it belongs to; without it
        // every error arrives as an unattributed sentence.
        return new ValidationResult(
            $"{validationContext.DisplayName} cannot be later than {latestAllowed:yyyy-MM-dd}.",
            validationContext.MemberName is null ? [] : [validationContext.MemberName]);
    }
}
