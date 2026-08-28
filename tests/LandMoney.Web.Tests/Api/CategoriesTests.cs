using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LandMoney.Web.Api;

namespace LandMoney.Web.Tests.Api;

/// <summary>#63's answer to "the eleven categories now exist in three places".</summary>
// The client's copy was removed -- it reads GET /api/categories -- and this is what
// keeps the two that are left equal. Without it, adding a twelfth category to
// categories.py gives a categorizer that answers a word the .NET side refuses to
// store and the dropdown never offers, with nothing red anywhere: the Python tests
// pass, the C# tests pass, and the disagreement is discovered by a person trying to
// label a row.
//
// Reading another language's source file from a unit test is not a thing to do
// lightly, and the alternative was a build step generating the C# from the Python.
// That lost on being a generator, a code-gen output to check in or to git-ignore,
// and a step in three places (here, ci.yml, the Dockerfile) for eleven strings that
// change about once a year. A test is the smallest thing that turns silent drift
// into a red build, and when it goes red the fix is to type a word into an array.
//
// What it costs, said plainly: this test knows the repository's layout, so moving
// either file breaks it -- as a missing-file failure naming both paths, which is the
// good kind. It is also the one test in this project that reads anything outside its
// own assembly. CLAUDE.md's "the tests need no Postgres, no Docker and no network"
// survives: a file on disk beside the test's own source is none of those.
public class CategoriesTests
{
    [Fact]
    public void The_vocabulary_is_the_one_the_categorizer_knows()
    {
        var fromPython = ReadCategoriesFromPython();

        // Sequence, not set. The order is display order and categories.py says so
        // in as many words -- grouped by how often the owner is expected to meet a
        // category -- so a re-sort here is a change to what the dropdown looks
        // like and should have to be made in both files.
        Assert.Equal(fromPython, Categories.All);
    }

    // A guard on the guard. A regex that silently matched nothing would leave the
    // assertion above comparing an empty list with an empty list, which passes and
    // checks nothing -- the shape of failure that makes a suite worth less than no
    // suite at all.
    [Fact]
    public void The_python_vocabulary_was_actually_read()
    {
        Assert.NotEmpty(ReadCategoriesFromPython());
    }

    [Theory]
    [InlineData("groceries")]
    [InlineData("other")]
    public void A_category_from_the_list_is_known(string category) =>
        Assert.True(Categories.IsKnown(category));

    // Case matters, and this is the test that says so on purpose rather than by
    // accident. Every category reaching this application comes from the
    // categorizer, which lower-cases its answer, or from the dropdown, which offers
    // these exact strings -- so "Groceries" is evidence of a client sending
    // something it did not get from here, and accepting it would put two spellings
    // of one category into a column the eval scorer reads.
    [Theory]
    [InlineData("Groceries")]
    [InlineData("GROCERIES")]
    [InlineData(" groceries")]
    [InlineData("groceries ")]
    [InlineData("takeaway")]
    [InlineData("")]
    [InlineData("unknown")]
    public void Anything_else_is_not(string category) =>
        Assert.False(Categories.IsKnown(category));

    // "unknown" above is the one worth a sentence. It is the sentinel a predictor
    // returns when it declines, and categories.py keeps it outside the vocabulary
    // deliberately so the scorer always counts it as a miss. It must not become a
    // twelfth value in transactions.category by arriving through this door either.

    private static IReadOnlyList<string> ReadCategoriesFromPython()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "categorizer", "src", "categorizer", "categories.py");

        Assert.True(
            File.Exists(path),
            $"categories.py was not found at {path}. It is the source of truth for the vocabulary "
            + "Categories.All copies; if it moved, this test and the comment on Categories.All "
            + "both have to move with it.");

        var source = File.ReadAllText(path);

        // Everything between `CATEGORIES ... = (` and the first `)`. Singleline so
        // `.` crosses the newlines, non-greedy so it stops at the closing bracket
        // of the tuple rather than at the end of the file. The tuple holds nothing
        // but quoted strings and commas -- the comments in that file are above the
        // assignment, not inside it -- which is what makes this safe without a
        // Python parser.
        var tuple = Regex.Match(
            source,
            @"CATEGORIES\s*:[^=]*=\s*\((.*?)\)",
            RegexOptions.Singleline);

        Assert.True(tuple.Success, $"No CATEGORIES tuple found in {path}.");

        return Regex.Matches(tuple.Groups[1].Value, "\"([^\"]*)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    /// <summary>The repository root, found at compile time rather than at run time.</summary>
    // [CallerFilePath] is the path of *this file* as the compiler saw it, so the
    // walk up is from a known place. Directory.GetCurrentDirectory() is the obvious
    // alternative and is the test runner's working directory -- bin/Debug/net10.0
    // today, something else under `dotnet test` with a different runner, and the
    // failure is a file-not-found naming a path nobody wrote.
    //
    // Four levels: Api -> LandMoney.Web.Tests -> tests -> the root.
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
}

/// <summary>#63's never-overwrite rule, which nothing else can exercise yet.</summary>
// The rule is that a prediction never writes over a human correction, and the issue
// allows it to be a rule that exists in code and is commented rather than one a
// scenario exercises -- because nothing re-categorises an existing row today, so
// there is no scenario to write. What is testable is the decision itself, which is a
// pure function, and testing it is what stops the rule being deleted as dead code by
// somebody who notices it can never currently be false.
public class CategorySourcesTests
{
    [Fact]
    public void A_human_correction_is_never_overwritten() =>
        Assert.False(CategorySources.MayOverwrite(CategorySources.Human));

    [Theory]
    [InlineData(null)]
    [InlineData("rules")]
    [InlineData("model")]
    public void Anything_else_may_be(string? source) =>
        Assert.True(CategorySources.MayOverwrite(source));

    // The null case above is the one with a decision in it. An unset source is a row
    // nothing has claimed -- imported by #62, or created while the categorizer was
    // down -- and those are exactly the rows a backfill exists to fill in. Refusing
    // them would make the guard mean "never write a category twice", which is a
    // different and much stronger rule than the one asked for.

    // Case-sensitively, and this matters more than it looks. The comparison is
    // Ordinal, so a source stored as "Human" by some future writer would be
    // overwritable -- which is the safe direction to be wrong in only because
    // nothing but the PATCH handler writes this value, and it writes the constant.
    [Theory]
    [InlineData("Human")]
    [InlineData("HUMAN")]
    public void The_comparison_is_exact(string source) =>
        Assert.True(CategorySources.MayOverwrite(source));
}
