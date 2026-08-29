using System.ComponentModel.DataAnnotations;
using System.Reflection;
using LandMoney.Web.Api;

namespace LandMoney.Web.Tests.Api;

/// <summary>#67. What a suggestion may be asked about, and what it may not.</summary>
// Two jobs, and the second is the one worth having. The first is ordinary: the
// rules refuse what the categorizer would refuse, so a caller gets a 400 naming
// the field instead of a 422 from a Python service it has never heard of. The
// second is the drift: these rules are a copy of CreateTransactionRequest's, and a
// copy nothing checks is a copy that has already started to rot -- which is the
// argument CategoriesTests makes about the eleven categories, applied to the four
// attributes that decide what an amount is.
public class CategorySuggestionRequestTests
{
    private static List<ValidationResult> Validate(CategorySuggestionRequest request)
    {
        var results = new List<ValidationResult>();

        // The same two arguments ValidationFilter<T> passes and the import path
        // passes, for the same reason: validateAllProperties: true, or everything
        // but [Required] is skipped and a negative amount sails through.
        Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        return results;
    }

    private static CategorySuggestionRequest Request(
        decimal amount = 42.50m, string currency = "EUR", string description = "Dinner") =>
        new() { Amount = amount, Currency = currency, Description = description };

    [Fact]
    public void An_ordinary_transaction_being_typed_is_accepted() =>
        Assert.Empty(Validate(Request()));

    [Theory]
    [InlineData(0)]        // what an empty amount field parses to, if a client sends one
    [InlineData(-12.50)]   // a debit copied from a statement
    public void An_amount_the_categorizer_would_refuse_is_refused_here(decimal amount)
    {
        // `Field(gt=0, ...)` in contracts.py. Refusing it here is not tidiness: a
        // 422 from the Python service reaches this side as a refused call, which is
        // logged as the service misbehaving when the truth is that this application
        // sent it something it should not have.
        Assert.NotEmpty(Validate(Request(amount: amount)));
    }

    [Fact]
    public void A_third_decimal_place_is_refused_because_the_other_side_refuses_it_too()
    {
        // `decimal_places=2`. The same rule the create path applies against the
        // column, arriving here for a different reason -- there is no column on this
        // path, and there is still a service that will not take it.
        Assert.NotEmpty(Validate(Request(amount: 12.345m)));
    }

    [Theory]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("1$x")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency) =>
        Assert.NotEmpty(Validate(Request(currency: currency)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_description_with_nothing_in_it_is_refused(string description)
    {
        // Three spaces is the one that needs [Required] rather than a minimum
        // length: RequiredAttribute trims before it checks and StringLength does
        // not. There is nothing here to categorise either way.
        Assert.NotEmpty(Validate(Request(description: description)));
    }

    [Fact]
    public void A_description_longer_than_the_form_allows_is_refused() =>
        Assert.NotEmpty(Validate(Request(description: new string('x', 501))));

    /// <summary>The rules are the create path's rules, and this is what says so.</summary>
    // The copy that exists here is deliberate -- the reasoning is on the record
    // itself -- and what makes it survivable is that a rule added to one and not the
    // other turns this red, naming both types, rather than showing up as a
    // suggestion that silently stops appearing for amounts the form still accepts.
    //
    // The attributes are compared by their *rendered* form rather than by type
    // alone, so a bound that changes on one side is caught as well as a rule that is
    // missing entirely. ValidationAttribute does not override ToString, so each one
    // is described by hand below; the alternative was comparing property by property
    // through reflection, which reads as a framework and answers the same question.
    //
    // OccurredAt is deliberately absent from this comparison and from the type: a
    // date tells a categorizer nothing, and a field an endpoint does not use is a
    // field a caller can be refused for getting wrong.
    [Theory]
    [InlineData(nameof(CategorySuggestionRequest.Amount))]
    [InlineData(nameof(CategorySuggestionRequest.Currency))]
    [InlineData(nameof(CategorySuggestionRequest.Description))]
    public void Every_rule_is_the_one_the_create_path_applies(string property)
    {
        var suggestion = RulesOn(typeof(CategorySuggestionRequest), property);
        var create = RulesOn(typeof(CreateTransactionRequest), property);

        Assert.Equal(create, suggestion);
    }

    private static List<string> RulesOn(Type type, string property)
    {
        var member = type.GetProperty(property)
            ?? throw new InvalidOperationException($"{type.Name} has no property {property}.");

        return [.. member
            .GetCustomAttributes<ValidationAttribute>(inherit: true)
            .Select(Describe)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>One attribute as a string, including whatever bounds it carries.</summary>
    // Only the attributes these three properties actually use are spelled out. A
    // fourth kind added to CreateTransactionRequest falls through to the last line,
    // which compares the type name and the message -- weaker than the branches
    // above, and still enough to fail when only one of the two records has it.
    private static string Describe(ValidationAttribute attribute) => attribute switch
    {
        RangeAttribute range =>
            $"Range({range.Minimum}..{range.Maximum}, "
            + $"invariant={range.ParseLimitsInInvariantCulture}, message={range.ErrorMessage})",
        StringLengthAttribute length =>
            $"StringLength({length.MinimumLength}..{length.MaximumLength})",
        RegularExpressionAttribute pattern =>
            $"RegularExpression({pattern.Pattern}, message={pattern.ErrorMessage})",
        RequiredAttribute required =>
            $"Required(allowEmpty={required.AllowEmptyStrings})",
        DecimalScaleAttribute scale => $"DecimalScale({scale.MaxScale})",
        _ => $"{attribute.GetType().Name}(message={attribute.ErrorMessage})",
    };
}
