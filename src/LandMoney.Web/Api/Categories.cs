using System.Collections.Frozen;

namespace LandMoney.Web.Api;

/// <summary>The closed category vocabulary, as this half of the application knows it.</summary>
// A copy. The list is decided in docs/evals.md and lives in
// src/categorizer/src/categorizer/categories.py, which is the file to edit when a
// twelfth category is agreed; this array is what the .NET side needs in order to
// refuse anything else. Nothing at runtime keeps the two equal --
// CategoriesTests.The_vocabulary_is_the_one_the_categorizer_knows reads that file
// and asserts this array against it, so drift is a red build naming this file
// rather than a label nobody can enter.
//
// #63 asked how the copies stay in step and said to decide or accept the drift out
// loud. There were three of them for about an hour: Python, here, and a third in
// the client's dropdown. The third is gone -- GET /api/categories serves this
// array and the client renders whatever it is given, so the screen cannot offer a
// category the server would refuse. Two copies, one of them checked. What lost:
// three copies with a comment on each (cheapest, and the failure it leaves live is
// a person labelling a row with a word the scorer then rejects, which is the exact
// thing a closed vocabulary exists to prevent); and one shared data file all three
// read, which is the only route with no copies at all and breaks a decision #39
// made -- the categorizer's Docker build context is src/categorizer, and nothing
// in it may reach outside its own folder.
//
// The order is categories.py's order, which is display order: grouped by how often
// the owner is expected to meet a category rather than alphabetically. The test
// compares the sequence and not the set, so re-sorting this array here is a
// failure rather than a tidy-up.
public static class Categories
{
    public static readonly IReadOnlyList<string> All =
    [
        "groceries",
        "eating-out",
        "transport",
        "housing",
        "health",
        "shopping",
        "subscriptions",
        "leisure",
        "gifts",
        "fees",
        "other",
    ];

    // Ordinal, and case-sensitive on purpose. Every category that reaches this
    // application comes either from the categorizer, which normalises its answer
    // to lower case before returning it, or from the dropdown, which offers these
    // exact strings. "Groceries" is therefore not a spelling to be forgiving
    // about; it is evidence that something is sending a value it did not get from
    // here, and accepting it would put two spellings of one category into a column
    // the eval scorer reads.
    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string category) => Known.Contains(category);
}

/// <summary>What may be written into <c>transactions.category_source</c>.</summary>
// The three producers of a category. "rules" and "model" are the categorizer's own
// words, arriving over HTTP and stored as sent -- they are declared here so that
// the never-overwrite rule below has something to compare against, not so that the
// wire value is translated. "human" is this application's, and is the only one of
// the three no service can send: it is written by the PATCH handler and nowhere
// else, which is what makes a correction distinguishable from a guess.
public static class CategorySources
{
    public const string Rules = "rules";
    public const string Model = "model";
    public const string Human = "human";

    /// <summary>May a prediction write over what is already stored?</summary>
    // #63: a human-set category is never overwritten by a later prediction. The
    // issue allows this to be a rule that exists in code and is commented rather
    // than one a scenario exercises, because nothing re-categorises an existing
    // row today -- and that is exactly the state in which the rule is easiest to
    // lose. It is a call somebody has to delete on purpose rather than a sentence
    // in a closed issue nobody reads again.
    //
    // Note which way the null goes. An unset source is a row nothing has claimed,
    // so a prediction may have it; only the string "human" refuses.
    public static bool MayOverwrite(string? existingSource) =>
        !string.Equals(existingSource, Human, StringComparison.Ordinal);
}
