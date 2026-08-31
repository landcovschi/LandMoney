using System.Globalization;
using System.Text;
using LandMoney.Web.Import;

namespace LandMoney.Web.Export;

/// <summary>One row on its way to <c>evals/transactions.csv</c>.</summary>
// Not TransactionResponse and not the entity. The entity carries an id, an owner
// and a created-at, none of which belong in an eval set -- and the response carries
// the source, which is the column this whole export filters on and therefore the
// one column that would be constant in every row of the file. A type with exactly
// the five fields the file holds is what makes "the shape of the CSV" a thing the
// compiler knows.
public sealed record LabelledRow(
    DateOnly OccurredAt,
    decimal Amount,
    string Currency,
    string Description,
    string Category);

/// <summary>The five columns of <c>evals/transactions.csv</c>, rendered.</summary>
// #89. The file this produces is **not** the file POST /api/transactions/import
// reads, and the difference is the fifth column: the import's four are what the
// application stores about a purchase, and this one adds the label a person put on
// it. evals/README.md documents the five and this class is where they are written
// down on this side of the wire.
//
// Deliberately a pure function over rows rather than something that writes to the
// response: it is the half of the endpoint with decisions in it -- the column
// order, the two formats, the sort -- and it is therefore the half worth testing
// without a database, which is the only kind of test this repository's suite runs.
public static class LabelledCsv
{
    /// <summary>The header, and it is the same five words evals/score.py checks for.</summary>
    // score.py compares its COLUMNS tuple against the header exactly rather than by
    // lookup, "so a renamed or reordered column is an error instead of a silent
    // column of None". Which means this string is a contract with a Python file two
    // folders away, and LabelledCsvTests reads that file to assert it -- the same
    // answer CategoriesTests gives for the vocabulary, for the same reason: two
    // copies, one of them checked, rather than two copies and a comment.
    public const string Header =
        TransactionCsv.OccurredAtColumn + ","
        + TransactionCsv.AmountColumn + ","
        + TransactionCsv.CurrencyColumn + ","
        + TransactionCsv.DescriptionColumn + ","
        + CategoryColumn;

    /// <summary>The column the import has not got.</summary>
    // Named here rather than beside the other four in TransactionCsv on purpose.
    // That class is what reads a file the application stores rows from, and it
    // reports `category` as an *ignored* column -- putting the name in there would
    // read as a promise that it is one day going to be read.
    public const string CategoryColumn = "category";

    /// <summary>Two decimal places, always, and never the ambient culture.</summary>
    // numeric(18,2) means Postgres hands back 78.50 where the CSV that produced it
    // said 78.5, so a plain ToString() would already print two places -- and relying
    // on that is relying on the scale surviving every layer between here and the
    // column. The format string says it instead. InvariantCulture is the rule this
    // repository has been bitten by twice from opposite directions (#31), and it
    // matters more on the way out than on the way in: a machine set to Romanian
    // would write 78,50, which is two fields, and evals/score.py would refuse the
    // whole file with a message about a row count.
    private const string AmountFormat = "0.00";

    public static string Render(IEnumerable<LabelledRow> rows)
    {
        var builder = new StringBuilder();

        builder.Append(Header).Append(CsvWriter.LineEnding);

        foreach (var row in rows)
        {
            CsvWriter.AppendLine(
                builder,
                row.OccurredAt.ToString(TransactionCsv.DateFormat, CultureInfo.InvariantCulture),
                row.Amount.ToString(AmountFormat, CultureInfo.InvariantCulture),
                row.Currency,
                row.Description,
                row.Category);
        }

        return builder.ToString();
    }

    /// <summary>What the file is called when it is saved.</summary>
    // Dated, because the export is a snapshot and the interesting question about one
    // sitting in a downloads folder is which corrections it predates. Not called
    // `transactions.csv`: that is the name of the file it is appended *to*, and
    // #89's third trap is that two files with different shapes and the same name get
    // swapped. `labelled` is the word that says which rows these are.
    public static string FileName(DateOnly today) =>
        $"labelled-{today.ToString(TransactionCsv.DateFormat, CultureInfo.InvariantCulture)}.csv";
}
