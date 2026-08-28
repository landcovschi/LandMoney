using System.ComponentModel.DataAnnotations;

namespace LandMoney.Web.Api;

/// <summary>The value is one of <see cref="Categories.All"/>, or null.</summary>
// The rule that makes a correction a *label* rather than an opinion. #63 asks for
// the closed list of eleven and not a free text box, and a dropdown on its own is
// not that rule -- it is a convenience for whoever is using the screen this
// afternoon. Anything can send this endpoint a body.
//
// Null is valid and means "clear it", which is a real answer and the same
// abstention the rules baseline already produces. The empty string is not: it is
// what an HTML <select> yields for a blank option, and letting it through would
// put a row in the table whose category is neither a category nor absent. The
// client turns its blank option into null before sending, and this is what says
// so if it ever stops.
//
// [AllowedValues] arrived in .NET 8 and does this in one line. It takes
// params object?[], which has to be a list of constants written inside the
// brackets -- so using it would mean the eleven strings typed a second time, in
// this file, next to the array they were copied from. That is a copy the test
// pinning Categories.All to categories.py would not see.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class KnownCategoryAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        // Nothing to check. Absence is handled by the property being `required`,
        // which System.Text.Json enforces while binding -- see UpdateCategoryRequest.
        if (value is null)
        {
            return ValidationResult.Success;
        }

        // Not a string at all. The binder cannot produce this, and the same guard
        // is in DecimalScaleAttribute for the same reason: an attribute put on the
        // wrong property should fail to say anything rather than report a category
        // problem about a date.
        if (value is not string category)
        {
            return ValidationResult.Success;
        }

        if (Categories.IsKnown(category))
        {
            return ValidationResult.Success;
        }

        // The list is in the message. It is eleven short words, the client already
        // has them, and a 400 reading "Category must be one of the eleven" would
        // send whoever is holding curl to go and find them.
        return new ValidationResult(
            $"{validationContext.DisplayName} must be one of: {string.Join(", ", Categories.All)}.",
            validationContext.MemberName is null ? [] : [validationContext.MemberName]);
    }
}
