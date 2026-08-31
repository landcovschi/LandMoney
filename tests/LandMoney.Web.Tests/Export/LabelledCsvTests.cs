using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LandMoney.Web.Export;
using LandMoney.Web.Import;

namespace LandMoney.Web.Tests.Export;

/// <summary>#89. The five columns, and the two formats that cross a boundary.</summary>
// The endpoint around this cannot be tested in process -- it reaches AppDbContext,
// which is the wall AuthorizationTests and #62 both document -- so everything with a
// decision in it was put here instead: the column order, the date and amount
// formats, the sort's effect on the file, and the header that has to match a Python
// file two folders away. What is left in the handler is a WHERE clause, an OrderBy
// and two headers, and those were checked by hand against the compose database.
public class LabelledCsvTests
{
    private static LabelledRow Row(
        string occurredAt = "2026-06-02",
        decimal amount = 412.50m,
        string currency = "MDL",
        string description = "linella",
        string category = "groceries") =>
        new(
            DateOnly.ParseExact(occurredAt, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            amount,
            currency,
            description,
            category);

    [Fact]
    public void The_header_is_the_five_columns_in_order() =>
        Assert.Equal("occurred_at,amount,currency,description,category", LabelledCsv.Header);

    // The fifth column is the whole difference between this file and the one
    // POST /api/transactions/import reads, which is #89's third trap. Asserted as a
    // relationship rather than as two strings, so that adding a column to the import
    // cannot silently leave the export describing four.
    [Fact]
    public void It_is_the_import_header_plus_the_category() =>
        Assert.Equal(
            TransactionCsv.HeaderExample + "," + LabelledCsv.CategoryColumn,
            LabelledCsv.Header);

    [Fact]
    public void A_file_is_the_header_and_one_line_per_row()
    {
        var csv = LabelledCsv.Render([
            Row(),
            Row(occurredAt: "2026-06-03", description: "cofee", category: "eating-out"),
        ]);

        Assert.Equal(
            "occurred_at,amount,currency,description,category\r\n"
            + "2026-06-02,412.50,MDL,linella,groceries\r\n"
            + "2026-06-03,412.50,MDL,cofee,eating-out\r\n",
            csv);
    }

    // An empty export is still a file with a header, and that is deliberate rather
    // than incidental: it is what makes `python evals/score.py --set <file>` a
    // command that reports "no rows" instead of one that reports a broken file. The
    // client is what declines to download it, and says why.
    [Fact]
    public void An_empty_export_is_the_header_alone() =>
        Assert.Equal("occurred_at,amount,currency,description,category\r\n", LabelledCsv.Render([]));

    // The file ends with a line ending, which is what makes appending it to
    // evals/transactions.csv produce a row rather than glue two rows together.
    [Fact]
    public void Every_line_is_terminated() =>
        Assert.EndsWith("\r\n", LabelledCsv.Render([Row()]), StringComparison.Ordinal);

    // Two decimal places always, matching numeric(18,2) and the file the rows are
    // appended to. 78.5 and 78.50 are different bit patterns of the same decimal --
    // #62's duplicate key turns on exactly that -- and only one of them is what the
    // eval set is written in.
    [Theory]
    [InlineData(78.5, "78.50")]
    [InlineData(78, "78.00")]
    [InlineData(6000, "6000.00")]
    [InlineData(0.01, "0.01")]
    public void An_amount_is_written_with_two_places(decimal amount, string expected) =>
        Assert.Contains($",{expected},", LabelledCsv.Render([Row(amount: amount)]), StringComparison.Ordinal);

    /// <summary>The rule this repository has been bitten by twice, on the way out this time.</summary>
    // **The two cultures are not interchangeable and picking only one leaves half the
    // rule untested**, which a mutation sweep is what said: writing the *date* in the
    // ambient culture survived a test that used ro-RO alone. `-` is a literal in a
    // custom format string rather than a separator placeholder, so ro-RO renders
    // yyyy-MM-dd exactly as the invariant culture does and the mutation changed
    // nothing visible.
    //
    // ro-RO is here for the amount: 412,50 is two CSV fields, so the export gains a
    // row of six columns where the header says five and score.py refuses the file.
    // ar-SA is here for the date, and it is #31 arriving on the way out -- its default
    // calendar is Umm al-Qura, so the same format string yields a *Hijri* year,
    // silently, for a row that is otherwise perfectly formed.
    [Theory]
    [InlineData("ro-RO")]
    [InlineData("ar-SA")]
    public void Neither_the_date_nor_the_amount_is_written_in_the_ambient_culture(string culture)
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Contains("2026-06-02,412.50,", LabelledCsv.Render([Row()]), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // The guard on the guard, and it is here because the theory above passed for a
    // year that was never Hijri. If ar-SA ever stops defaulting to Umm al-Qura on
    // this runtime -- or the culture is unavailable on a stripped image and silently
    // falls back to the invariant one -- the date half of that theory becomes a test
    // of nothing, and this is what says so instead.
    [Fact]
    public void The_ambient_culture_used_above_really_does_render_a_different_year()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            Assert.DoesNotContain(
                "2026",
                new DateOnly(2026, 6, 2).ToString(TransactionCsv.DateFormat, CultureInfo.CurrentCulture),
                StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // A description holding a comma is the row #62's own verification names, and
    // unquoted it would be six fields where the header says five.
    [Fact]
    public void A_description_that_needs_quoting_gets_it() =>
        Assert.Contains(
            "MDL,\"lidl, centru\",groceries",
            LabelledCsv.Render([Row(description: "lidl, centru")]),
            StringComparison.Ordinal);

    // Rendered in the order given, with nothing sorted here. The order is the
    // query's -- ascending by day, so an export appended to evals/transactions.csv
    // keeps it in date order -- and this asserts that this function does not hold a
    // second opinion about it.
    [Fact]
    public void The_order_given_is_the_order_written()
    {
        var csv = LabelledCsv.Render([
            Row(occurredAt: "2026-06-03", description: "second"),
            Row(occurredAt: "2026-06-02", description: "first"),
        ]);

        Assert.True(
            csv.IndexOf("second", StringComparison.Ordinal) < csv.IndexOf("first", StringComparison.Ordinal),
            "Render sorted the rows. The sort belongs to the query, which orders by day ascending.");
    }

    [Fact]
    public void The_file_is_named_after_the_day_it_was_taken() =>
        Assert.Equal("labelled-2026-08-31.csv", LabelledCsv.FileName(new DateOnly(2026, 8, 31)));

    // Not `transactions.csv`, which is #89's third trap in one assertion: that is the
    // name of the file this one is appended to, and two files with different shapes
    // and the same name is how they get swapped.
    [Fact]
    public void The_file_is_not_named_after_the_one_it_is_appended_to() =>
        Assert.DoesNotContain(
            "transactions",
            LabelledCsv.FileName(new DateOnly(2026, 8, 31)),
            StringComparison.Ordinal);

    /// <summary>The header is a contract with a Python file, so the Python file is read.</summary>
    // score.py compares its COLUMNS tuple against the header exactly rather than by
    // lookup, so a column renamed on either side makes every export unreadable by
    // the one program that is meant to read it -- with nothing red anywhere, because
    // each side is self-consistent. Same answer CategoriesTests gives for the
    // vocabulary, for the same reason, with the same cost: this test knows the
    // repository's layout and fails naming both paths if either file moves.
    [Fact]
    public void The_header_is_the_one_the_scorer_expects() =>
        Assert.Equal(LabelledCsv.Header, string.Join(",", ReadColumnsFromPython()));

    // The guard on the guard. A regex that matched nothing would leave the assertion
    // above comparing an empty string with a header, and the failure would read as a
    // renamed column rather than as a broken test.
    [Fact]
    public void The_python_columns_were_actually_read() =>
        Assert.Equal(5, ReadColumnsFromPython().Count);

    private static IReadOnlyList<string> ReadColumnsFromPython()
    {
        var path = Path.Combine(RepositoryRoot(), "evals", "score.py");

        Assert.True(
            File.Exists(path),
            $"score.py was not found at {path}. It is the program this export exists to feed, "
            + "and its COLUMNS tuple is what LabelledCsv.Header has to match; if it moved, "
            + "this test moves with it.");

        var source = File.ReadAllText(path);

        // Everything between `COLUMNS = (` and the first `)`. The tuple holds nothing
        // but quoted strings and commas, which is what makes this safe without a
        // Python parser -- the same assumption CategoriesTests makes about the
        // CATEGORIES tuple, and it is checked by the count above.
        var tuple = Regex.Match(source, @"COLUMNS\s*=\s*\((.*?)\)", RegexOptions.Singleline);

        Assert.True(tuple.Success, $"No COLUMNS tuple found in {path}.");

        return Regex.Matches(tuple.Groups[1].Value, "\"([^\"]*)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    // Four levels: Export -> LandMoney.Web.Tests -> tests -> the root. The same walk
    // CategoriesTests makes, for the same reason: [CallerFilePath] is a path the
    // compiler knew, where the runner's working directory is not.
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
}
