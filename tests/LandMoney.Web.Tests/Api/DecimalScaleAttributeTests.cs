using System.ComponentModel.DataAnnotations;
using System.Globalization;
using LandMoney.Web.Api;

namespace LandMoney.Web.Tests.Api;

/// <summary>The rule from #19: an amount the database would have to round is refused.</summary>
// Every case here is written as a string and parsed, because decimal is not one of
// the types a C# attribute argument may be -- [InlineData(12.34)] compiles as a
// double and arrives having already lost the thing under test. InvariantCulture on
// the parse for the reason CreateTransactionRequest already writes down about
// [Range]: a machine set to Romanian or German reads "0.01" as 1.
public class DecimalScaleAttributeTests
{
    private static readonly DecimalScaleAttribute TwoPlaces = new(2);

    [Theory]
    [InlineData("12.34")]                 // exactly the scale the column holds
    [InlineData("12.3")]                  // fewer
    [InlineData("12")]                    // none at all
    [InlineData("0.01")]                  // the smallest amount the API accepts
    [InlineData("-12.34")]                // the sign is not this rule's business
    [InlineData("9999999999999999.99")]   // the largest numeric(18,2) holds
    public void An_amount_the_column_stores_unchanged_is_accepted(string literal) =>
        Assert.Null(TwoPlaces.GetValidationResult(Parse(literal), Context()));

    [Theory]
    [InlineData("12.30")]
    [InlineData("12.300")]
    [InlineData("12.3000000000")]
    public void A_trailing_zero_is_precision_about_nothing_and_is_accepted(string literal) =>
        Assert.Null(TwoPlaces.GetValidationResult(Parse(literal), Context()));

    // This is the case a digit count gets wrong, and it is why the attribute
    // compares against the rounded value instead. decimal keeps the scale it was
    // parsed with -- 12.300m holds three decimal places and equals 12.3m -- so
    // counting them refuses a number the column stores perfectly.
    [Fact]
    public void The_trailing_zeros_really_are_there_or_the_test_above_proves_nothing()
    {
        Assert.Equal(3, decimal.GetBits(Parse("12.300"))[3] >> 16 & 0xFF);
        Assert.Equal(Parse("12.3"), Parse("12.300"));
    }

    [Theory]
    [InlineData("12.345")]     // the value #19 found: stored as 12.35, reported as 12.345
    [InlineData("12.341")]     // rounds down rather than up, and is refused just the same
    [InlineData("0.001")]
    [InlineData("-12.345")]
    public void An_amount_the_column_would_round_is_refused(string literal) =>
        Assert.NotNull(TwoPlaces.GetValidationResult(Parse(literal), Context()));

    // Absence belongs to [Required] and a wrong type belongs to the compiler.
    // Failing on either would send a 400 that no client can act on.
    [Fact]
    public void An_absent_value_is_left_to_Required() =>
        Assert.Null(TwoPlaces.GetValidationResult(null, Context()));

    [Theory]
    [InlineData("12.345")]
    [InlineData(12345)]
    [InlineData(12.345d)]
    public void A_value_that_is_not_a_decimal_is_not_this_attributes_problem(object value) =>
        Assert.Null(TwoPlaces.GetValidationResult(value, Context()));

    // The member name is what files the message under "amount" in the 400 body
    // rather than under the empty key, which is where a form looks for it.
    [Fact]
    public void The_failure_names_the_member_it_came_from()
    {
        var result = TwoPlaces.GetValidationResult(Parse("12.345"), Context());

        Assert.NotNull(result);
        Assert.Equal(["Amount"], result.MemberNames);
        Assert.Equal("Amount cannot have more than 2 decimal places.", result.ErrorMessage);
    }

    // MemberName is null whenever the attribute is reached other than through a
    // property -- a parameter, or a direct Validator.TryValidateValue. The
    // attribute passes an empty array there rather than a one-element array
    // holding null, which is what the ternary inside it is for.
    [Fact]
    public void A_context_with_no_member_name_still_reports_the_failure()
    {
        var context = new ValidationContext(new object()) { DisplayName = "Amount" };

        var result = TwoPlaces.GetValidationResult(Parse("12.345"), context);

        Assert.NotNull(result);
        Assert.Empty(result.MemberNames);
    }

    // The scale is an argument, not a constant, and nothing else in the repository
    // passes anything but 2 -- so this is the only place the argument is shown to
    // be read at all.
    [Fact]
    public void The_allowed_scale_is_the_one_the_attribute_was_given()
    {
        var fourPlaces = new DecimalScaleAttribute(4);

        Assert.Null(fourPlaces.GetValidationResult(Parse("12.3456"), Context()));
        Assert.NotNull(fourPlaces.GetValidationResult(Parse("12.34567"), Context()));
    }

    private static ValidationContext Context() => ValidationContexts.ForMember("Amount");

    private static decimal Parse(string literal) =>
        decimal.Parse(literal, CultureInfo.InvariantCulture);
}
