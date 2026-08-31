using System.Text;

namespace LandMoney.Web.Export;

/// <summary>Writes RFC 4180 CSV. The mirror of <see cref="Import.CsvReader"/>, and deliberately not in the same file.</summary>
// #89. Reading and writing CSV are the same format and not the same job, and the
// two live apart for the reason the issue names as its third trap: the file
// POST /api/transactions/import reads has four columns and no category, and the
// file this writes has five. They are different files with different jobs, and the
// cheapest way to swap them is to make one class responsible for both.
//
// What is shared is the *format* and it is shared by both obeying RFC 4180 rather
// than by a base class: CsvReaderTests names doubled quotes, embedded newlines and
// both line endings, and the round-trip test in CsvWriterTests is what asserts that
// this produces what that reads. A shared abstraction over 30 lines of quoting
// would be the wrong direction -- the reader is lenient about line endings on
// purpose and this is not, and a common type would have to hold both opinions.
public static class CsvWriter
{
    /// <summary>The line ending written, and it is CRLF rather than LF on purpose.</summary>
    // RFC 4180 says CRLF, which is the weaker half of the reason. The stronger one
    // is that evals/transactions.csv is CRLF in the working tree -- core.autocrlf
    // is true on this machine and git stores it as LF -- so an export appended to
    // it is homogeneous with what is already there rather than a block of LF rows
    // in the middle of a CRLF file. Nothing downstream cares: Python's csv module
    // reads both, and so does CsvReader.
    public const string LineEnding = "\r\n";

    private const char Quote = '"';

    // The four RFC 4180 characters plus the two that decide the leading/trailing
    // space rule below. Written as a const rather than a char[] literal at the call
    // site so IndexOfAny does not allocate one per field.
    private static readonly char[] MustQuote = [',', Quote, '\r', '\n'];

    /// <summary>One field, quoted if it has to be, with any quote inside it doubled.</summary>
    public static string Field(string value)
    {
        // Leading and trailing whitespace is quoted although RFC 4180 does not
        // require it, and the reason is a reader this repository does not own.
        // TransactionCsv trims every field it reads, so a round trip through the
        // import path cannot tell the difference -- but evals/score.py uses
        // Python's csv module, which does not trim, so a description stored with a
        // trailing space would arrive there with one and the eval row would differ
        // from the transaction it came from by an invisible character. Quoting
        // makes the file say which it meant.
        var needsQuoting =
            value.IndexOfAny(MustQuote) >= 0
            || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));

        if (!needsQuoting)
        {
            return value;
        }

        return string.Concat(Quote, value.Replace("\"", "\"\"", StringComparison.Ordinal), Quote);
    }

    /// <summary>Appends one row and its line ending.</summary>
    // A StringBuilder rather than a string return, because the caller is writing a
    // whole file: string.Join per row and a Join of the joins allocates every row
    // twice for a file that is one buffer either way.
    public static void AppendLine(StringBuilder builder, params ReadOnlySpan<string> fields)
    {
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Field(fields[index]));
        }

        builder.Append(LineEnding);
    }
}
