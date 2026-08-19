using System.ComponentModel.DataAnnotations;

namespace LandMoney.Web.Api;

/// <summary>
/// Refuses a <see cref="decimal"/> carrying more than <see cref="MaxScale"/> decimal
/// places, so the database is never asked to round an amount.
/// </summary>
// Added in review, and the finding behind it is worth keeping. [Range] bounds how
// large an amount may be but says nothing about how precise it is, and
// numeric(18,2) does not refuse a third decimal place -- it rounds it away in
// silence. The write path then never saw the stored value: the 201 was built from
// the in-memory entity and reported 12.345, while the row held 12.35 and every
// later GET agreed with the row. An amount that disagrees with itself is trusted
// until someone happens to read it twice.
//
// This carries the rule the ceiling already follows -- validation limits are kept
// equal to the column's, so the database never has to be the one to say no --
// down to the scale, where it had been missed.
//
// Rounding the input instead was the alternative and is worse: it turns a
// client's typo into a silent edit of an amount. Money is the last place to
// quietly improve on what someone typed. Rejecting hands the decision back.
//
// It also removes the need to re-read the row after SaveChanges to build an
// honest response. Once no value can be rounded on the way in, the in-memory
// entity and the stored row cannot disagree, so ToResponse stays truthful without
// a second round trip.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class DecimalScaleAttribute : ValidationAttribute
{
    public DecimalScaleAttribute(int maxScale) => MaxScale = maxScale;

    /// <summary>Decimal places allowed. Two, to match numeric(18,2).</summary>
    public int MaxScale { get; }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        // Absence belongs to [Required], and a non-decimal means the attribute is
        // on the wrong property -- neither is this rule's to report. Same
        // reasoning as PlausibleDateAttribute.
        if (value is null || value is not decimal amount)
        {
            return ValidationResult.Success;
        }

        // Comparing against the rounded value rather than counting digits, because
        // decimal keeps its trailing zeros: 12.30m and 12.3m are equal but carry
        // different scales, and a digit count would reject "12.300" for being
        // precise about nothing. Equality after rounding asks the question that
        // actually matters -- would storing this change it?
        //
        // The rounding mode does not matter here. decimal.Round defaults to
        // banker's rounding and Postgres rounds half away from zero, so the two
        // disagree on 12.345 (12.34 against 12.35) -- but both differ from the
        // original, which is the only thing being tested.
        if (decimal.Round(amount, MaxScale) == amount)
        {
            return ValidationResult.Success;
        }

        return new ValidationResult(
            $"{validationContext.DisplayName} cannot have more than {MaxScale} decimal places.",
            validationContext.MemberName is null ? [] : [validationContext.MemberName]);
    }
}
