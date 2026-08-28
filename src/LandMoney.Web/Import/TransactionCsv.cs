using System.Globalization;
using LandMoney.Web.Api;

namespace LandMoney.Web.Import;

/// <summary>One row of the file: either a request to create, or a reason it cannot be.</summary>
// Exactly one of the two is non-null. A discriminated union is what this wants and
// C# has none, so the invariant is written here and enforced by the two factory
// methods rather than by the type -- which is the honest version of the trade
// rather than a nullable pair nobody explains.
public sealed record ImportRow(int LineNumber, CreateTransactionRequest? Request, string? Reason)
{
    public static ImportRow Parsed(int lineNumber, CreateTransactionRequest request) =>
        new(lineNumber, request, null);

    public static ImportRow Rejected(int lineNumber, string reason) =>
        new(lineNumber, null, reason);
}

/// <summary>A whole file, once its header has been understood.</summary>
public sealed record ParsedFile(IReadOnlyList<string> IgnoredColumns, IReadOnlyList<ImportRow> Rows);

/// <summary>Reads the four columns this application stores out of a CSV file.</summary>
// The columns are the ones evals/transactions.csv already uses, which is the point
// of #62: the file converted by hand from a bank export is the same file that later
// becomes the eval set, with a `category` column added and labelled by hand.
//
// This class answers *shape* -- is that a date, is that a number, does the row have
// the right number of fields. It does not answer *rules*: whether the amount is
// positive, whether the date is plausible, whether the currency is three letters.
// Those live on CreateTransactionRequest and are run by the endpoint through the
// same Validator call ValidationFilter<T> makes, so the import and the single-row
// POST cannot drift apart and the messages are written once.
public static class TransactionCsv
{
    public const string OccurredAtColumn = "occurred_at";
    public const string AmountColumn = "amount";
    public const string CurrencyColumn = "currency";
    public const string DescriptionColumn = "description";

    /// <summary>The only date format accepted, in either direction.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>What the header must contain. Quoted in every message about the header.</summary>
    public const string HeaderExample = "occurred_at,amount,currency,description";

    private static readonly string[] RequiredColumns =
        [OccurredAtColumn, AmountColumn, CurrencyColumn, DescriptionColumn];

    // AllowThousands is deliberately absent, and its absence is the entire mechanism
    // by which #62's currency decision is enforced rather than merely stated.
    // NumberStyles.Number -- the obvious constant, and the default for
    // decimal.Parse -- includes it, and under InvariantCulture that reads "1,50"
    // as one hundred and fifty, silently, which is the exact shape of the bug #31
    // exists to prevent. Without it the same text is not a number and the row is
    // refused by name.
    //
    // AllowLeadingSign is deliberately present, for the mirror-image reason. A bank
    // export writes a debit as -412.50; parsing it and letting [Range] refuse it
    // produces "Amount must be between 0.01 and ...", which names the real problem.
    // Refusing it here would say "not a number", which sends the reader to look for
    // a typo that is not there.
    private const NumberStyles AmountStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    /// <summary>How much of a bad value is quoted back in a message.</summary>
    // A description may be 500 characters and a mangled file may be one field of
    // 100,000. Neither belongs whole in an error list the browser has to render.
    private const int MaxQuotedLength = 40;

    /// <summary>Reads the file. Throws <see cref="CsvFormatException"/> for a header nothing can be done with.</summary>
    public static ParsedFile Parse(string text)
    {
        var rows = CsvReader.Read(text);

        if (rows.Count == 0)
        {
            throw new CsvFormatException(
                $"The file is empty. The first line must be the header: {HeaderExample}");
        }

        var header = rows[0];

        // Lower-cased and trimmed, so a spreadsheet that title-cased the header is
        // still read. Not otherwise normalised: `Occurred At` is not `occurred_at`,
        // because guessing at that mapping is how a column silently becomes the
        // wrong column.
        var names = header.Fields
            .Select(name => name.Trim().ToLowerInvariant())
            .ToArray();

        var duplicated = names
            .Where(name => name.Length > 0)
            .GroupBy(name => name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicated.Length > 0)
        {
            throw new CsvFormatException(
                $"The header names {string.Join(", ", duplicated)} more than once, "
                + "so there is no way to tell which column was meant.");
        }

        var missing = RequiredColumns.Where(required => !names.Contains(required)).ToArray();

        if (missing.Length > 0)
        {
            throw new CsvFormatException(
                $"The header is missing {string.Join(", ", missing)}. Expected: {HeaderExample}");
        }

        var occurredAtIndex = Array.IndexOf(names, OccurredAtColumn);
        var amountIndex = Array.IndexOf(names, AmountColumn);
        var currencyIndex = Array.IndexOf(names, CurrencyColumn);
        var descriptionIndex = Array.IndexOf(names, DescriptionColumn);

        // Reported rather than refused, and reported rather than ignored in silence.
        // The file this feature exists to read is evals/transactions.csv, which
        // carries a fifth `category` column -- refusing it would make the importer
        // unable to read the one file the issue names, and dropping it quietly would
        // let somebody believe their labels had been imported.
        var ignoredColumns = header.Fields
            .Where((_, index) => !RequiredColumns.Contains(names[index]))
            .Select(name => name.Trim())
            .ToArray();

        var parsed = new List<ImportRow>(rows.Count - 1);

        foreach (var row in rows.Skip(1))
        {
            // Strict in both directions. A row with too few fields would read the
            // wrong column into the wrong field for everything after the gap; a row
            // with too many is a description holding an unquoted comma, and guessing
            // which comma was meant is not this reader's job.
            if (row.Fields.Count != names.Length)
            {
                parsed.Add(ImportRow.Rejected(
                    row.LineNumber,
                    $"The row has {row.Fields.Count} fields; the header has {names.Length}. "
                    + "A description containing a comma has to be wrapped in quotes."));
                continue;
            }

            var reasons = new List<string>();

            var rawOccurredAt = row.Fields[occurredAtIndex].Trim();

            // ParseExact against one format, not TryParse. A column holding
            // "2026-06-02T14:33:00" is refused rather than truncated, which is
            // #62's "truncated deliberately, not converted" answered by declining
            // to convert: OccurredAt is a day, and a timestamp carries a zone this
            // file does not state, so there is no correct day to derive from it
            // without being told which zone it was written in.
            if (!DateOnly.TryParseExact(
                    rawOccurredAt, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurredAt))
            {
                reasons.Add(
                    $"{OccurredAtColumn} {Quote(rawOccurredAt)} is not a date written as {DateFormat}. "
                    + "A column holding a time as well has to have the time removed first.");
            }

            var rawAmount = row.Fields[amountIndex].Trim();

            if (!decimal.TryParse(rawAmount, AmountStyles, CultureInfo.InvariantCulture, out var amount))
            {
                reasons.Add(
                    $"{AmountColumn} {Quote(rawAmount)} is not a number. Write it the invariant way: "
                    + "a full stop for the decimal point and no thousands separator, as in 1234.56.");
            }

            if (reasons.Count > 0)
            {
                parsed.Add(ImportRow.Rejected(row.LineNumber, string.Join(" ", reasons)));
                continue;
            }

            // Currency is left exactly as typed rather than upper-cased here, so
            // that the endpoint upper-cases it in the same line CreateAsync does
            // and there is one such line per path. Trimmed only.
            parsed.Add(ImportRow.Parsed(row.LineNumber, new CreateTransactionRequest
            {
                OccurredAt = occurredAt,
                Amount = amount,
                Currency = row.Fields[currencyIndex].Trim(),
                Description = row.Fields[descriptionIndex].Trim(),
            }));
        }

        return new ParsedFile(ignoredColumns, parsed);
    }

    /// <summary>A value quoted back into a message, shortened and on one line.</summary>
    // ReplaceLineEndings because a quoted field may legitimately hold a newline, and
    // a message broken across lines in the middle of a list of them is unreadable.
    private static string Quote(string value)
    {
        var flattened = value.ReplaceLineEndings(" ");

        return flattened.Length <= MaxQuotedLength
            ? $"'{flattened}'"
            : $"'{flattened[..MaxQuotedLength]}...'";
    }
}
