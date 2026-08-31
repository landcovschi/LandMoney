using System.Linq.Expressions;
using LandMoney.Web.Api;
using LandMoney.Web.Export;
using LandMoney.Web.Models;

namespace LandMoney.Web.Tests.Export;

/// <summary>#89's first trap, which is the whole issue in one predicate.</summary>
// "A `model` row exported into the eval set is the predictor grading its own past
// answers, and the number afterwards means nothing." Nothing about that failure is
// visible from outside: the file has the right columns, the right shape and
// plausible labels, and the score it produces is simply wrong in the flattering
// direction.
//
// The predicate is compiled and run against ordinary objects, which is the only kind
// of test this suite can give it -- the handler that applies it reaches AppDbContext,
// which is Postgres, which is the property #22 defends. What that leaves out is
// written on LabelledRows.ByHand itself and was checked by hand: that the query calls
// this at all.
public class LabelledRowsTests
{
    private static readonly Func<Transaction, bool> ByHand = LabelledRows.ByHand.Compile();

    private static Transaction Row(string? category, string? source) => new()
    {
        Currency = "MDL",
        Description = "linella",
        Category = category,
        CategorySource = source,
    };

    [Fact]
    public void A_row_a_person_corrected_is_exported() =>
        Assert.True(ByHand(Row("groceries", CategorySources.Human)));

    // The two that must not be, and the reason the issue calls this the single
    // mistake it exists to avoid. Both are answers the categorizer produced, so
    // scoring the categorizer against them measures how well it agrees with itself.
    [Theory]
    [InlineData(CategorySources.Rules)]
    [InlineData(CategorySources.Model)]
    public void A_row_the_categorizer_guessed_is_not(string source) =>
        Assert.False(ByHand(Row("groceries", source)));

    // A row nothing has claimed. #62 stores every imported row this way -- the import
    // does not call the categorizer -- so this is not a rare state, it is most of a
    // freshly imported year.
    [Fact]
    public void A_row_with_no_source_is_not() =>
        Assert.False(ByHand(Row(category: null, source: null)));

    // The invariant #59 established, tested from the side that would break the export
    // rather than from the side that is true today. A `human` source with no category
    // cannot currently exist -- clearing a category clears both columns -- and if it
    // ever did, exporting it would put an empty fifth field into the file, which
    // score.py refuses for the whole file rather than for the row.
    [Fact]
    public void A_human_source_with_no_category_is_not_exported() =>
        Assert.False(ByHand(Row(category: null, source: CategorySources.Human)));

    // Ordinal, matching CategorySources.MayOverwrite next door. Nothing but the PATCH
    // handler writes this column and it writes the constant, so a differently-cased
    // value is evidence of a writer this application does not have -- and leaving it
    // out of the export is the safe direction to be wrong in.
    [Theory]
    [InlineData("Human")]
    [InlineData("HUMAN")]
    [InlineData("human ")]
    public void The_comparison_is_exact(string source) =>
        Assert.False(ByHand(Row("groceries", source)));

    // An Expression and not a Func, so that EF translates it into the WHERE clause
    // rather than fetching the table and filtering it here. The two compile
    // identically at the call site and only one of them is a query.
    [Fact]
    public void It_is_something_EF_can_translate() =>
        Assert.IsAssignableFrom<LambdaExpression>(LabelledRows.ByHand);
}
