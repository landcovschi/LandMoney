using System.ComponentModel.DataAnnotations;
using LandMoney.Web.Api;

namespace LandMoney.Web.Tests.Api;

/// <summary>#63. The rule that makes a correction a label rather than an opinion.</summary>
// The dropdown is not this rule. It is a convenience for whoever is using the screen
// this afternoon, and anything at all can send this endpoint a body -- so the closed
// list has to be enforced where the value arrives, not where it is chosen.
public class KnownCategoryAttributeTests
{
    private static readonly KnownCategoryAttribute Attribute = new();

    [Theory]
    [InlineData("groceries")]
    [InlineData("eating-out")]
    [InlineData("other")]
    public void A_category_from_the_closed_list_is_accepted(string category) =>
        Assert.Null(Validate(category));

    /// <summary>Clearing it back to none is a real answer, not a missing value.</summary>
    // "I do not know either" is the same abstention the rules baseline produces, and
    // #63 asks for it explicitly. Absence of the field is a different thing and is
    // refused elsewhere -- UpdateCategoryRequest.Category is `required`, which
    // System.Text.Json enforces while binding, before this attribute is reached.
    [Fact]
    public void Null_is_accepted_because_it_clears_the_category() =>
        Assert.Null(Validate(null));

    /// <summary>The empty string is not null and is refused.</summary>
    // What an HTML <select> yields for a blank option. The client turns its blank
    // option into null before sending; this is the test that says what happens if it
    // ever stops, and the answer must not be a row whose category is neither a
    // category nor absent.
    [Fact]
    public void The_empty_string_is_refused() => Assert.NotNull(Validate(string.Empty));

    [Theory]
    [InlineData("Groceries")]
    [InlineData("takeaway")]
    [InlineData("food")]
    [InlineData("unknown")]
    [InlineData("a category I made up")]
    public void Anything_outside_the_list_is_refused(string category) =>
        Assert.NotNull(Validate(category));

    // "food" and "takeaway" are the two worth naming. Neither is a typo -- they are
    // reasonable words for a category, and mapping them to eating-out is exactly the
    // synonym table anthropic_predictor.py refuses to own for the same reason: a
    // synonym is this application answering a question the vocabulary already
    // answered, and an open vocabulary is what docs/evals.md exists to refuse.

    /// <summary>The message names the field and lists what would have been accepted.</summary>
    // The member name is what ValidationFilter<T> camelCases into the key of the 400
    // body, and the client matches that key against its own field name to decide
    // where to put the sentence. #52 records what a mismatch looks like: the message
    // is correct, visible, and in the banner at the top instead of beside the
    // control that produced it.
    [Fact]
    public void The_message_names_the_field_and_the_eleven()
    {
        var result = Validate("nonsense");

        Assert.NotNull(result);
        Assert.Equal(["Category"], result.MemberNames);
        Assert.StartsWith("Category must be one of: ", result.ErrorMessage);

        // The list itself, rather than a count. Eleven short words in a 400 is
        // cheaper than sending whoever is holding curl to go and find them.
        foreach (var category in Categories.All)
        {
            Assert.Contains(category, result.ErrorMessage);
        }
    }

    /// <summary>On a property that is not a string, it says nothing.</summary>
    // The binder cannot produce this -- the property is declared string? -- so the
    // only way here is the attribute being put on the wrong property. The same guard
    // is in DecimalScaleAttribute for the same reason: an attribute in the wrong
    // place should fail to have an opinion rather than report a category problem
    // about a date.
    [Fact]
    public void A_value_that_is_not_a_string_is_left_alone() =>
        Assert.Null(Attribute.GetValidationResult(42, ValidationContexts.ForMember("Category")));

    private static ValidationResult? Validate(string? category) =>
        Attribute.GetValidationResult(category, ValidationContexts.ForMember("Category"));
}
