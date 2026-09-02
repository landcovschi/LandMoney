using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LandMoney.Web.Categorizing;

namespace LandMoney.Web.Tests.Categorizing;

/// <summary>#93's answer to "the batch cap now exists in two languages".</summary>
// The Python side refuses a batch over MAX_BATCH_ITEMS with a 422. From the .NET
// side that refusal is `refused`, which reads in a log as the categorizer
// misbehaving rather than as a number in appsettings.json -- and it would repeat on
// every tick for ever, leaving every row in the batch owed a category and one
// attempt poorer. The whole failure is silent in the way this project keeps meeting:
// nothing goes red, categories simply stop arriving.
//
// So the number is written down twice and pinned here, which is exactly the answer
// CategoriesTests gives for the eleven categories, with the same cost: this test
// knows the repository's layout, and moving either file breaks it as a
// missing-file failure naming both paths. That is the good kind.
//
// What lost is the same alternative that lost there -- generating one from the other
// -- and one more that is specific to this: asking the service for its own limit,
// which is a request on a start-up path against a container that scales to zero, in
// order to learn a number that changes about never.
public class CategorizerBatchCapTests
{
    [Fact]
    public void The_cap_is_the_one_the_categorizer_enforces()
    {
        Assert.Equal(CategorizerBatch.MaxItems, ReadCapFromPython());
    }

    // A guard on the guard. A regex that silently matched nothing would leave the
    // assertion above comparing a default with a default -- the shape of failure
    // that makes a suite worth less than no suite at all. CategoriesTests carries
    // the same pair for the same reason.
    [Fact]
    public void The_python_cap_was_actually_read()
    {
        Assert.True(ReadCapFromPython() > 0);
    }

    // The clamp is what makes the pin above worth having: a number in configuration
    // is not a number anyone re-reads, and going over the cap is the one mistake
    // that fails on every tick with no way to tell from a categorizer that is down.
    [Theory]
    [InlineData(20, 20)]
    [InlineData(CategorizerBatch.MaxItems, CategorizerBatch.MaxItems)]
    [InlineData(CategorizerBatch.MaxItems + 1, CategorizerBatch.MaxItems)]
    [InlineData(5000, CategorizerBatch.MaxItems)]
    public void A_batch_size_over_the_cap_is_held_at_it(int configured, int expected)
    {
        Assert.Equal(expected, CategorizerBatch.HeldWithinOneRequest(configured));
    }

    // Zero would make every tick a query claiming nothing, which looks exactly like
    // a categorizer that is never reached. The setting that turns categorising after
    // the fact off is the interval, and it says so in the log when it does.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_batch_size_of_nothing_is_held_at_one(int configured)
    {
        Assert.Equal(1, CategorizerBatch.HeldWithinOneRequest(configured));
    }

    private static int ReadCapFromPython()
    {
        var path = Path.Combine(
            RepositoryRoot(), "src", "categorizer", "src", "categorizer", "contracts.py");

        var match = Regex.Match(
            File.ReadAllText(path),
            // No end-of-line anchor. Every tracked file here is CRLF, and in .NET a
            // Multiline anchor matches immediately before the line feed -- with the
            // carriage return still in the way, so an anchored pattern would match
            // nothing on this machine and everything in a container. The test above is
            // what caught it.
            @"^MAX_BATCH_ITEMS\s*=\s*(\d+)",
            RegexOptions.Multiline);

        // Zero rather than a throw, so the failure is the assertion above naming both
        // numbers rather than an exception naming a regex.
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    // [CallerFilePath] is the path of *this file* as the compiler saw it, so the walk
    // up is from a known place rather than from the test runner's working directory.
    // Four levels: Categorizing -> LandMoney.Web.Tests -> tests -> the root.
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
}
