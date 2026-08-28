using System.Text;

namespace LandMoney.Web.Import;

/// <summary>One row of a CSV file, and the line of the file it started on.</summary>
// LineNumber is the *physical* line, 1-based, header included, because that is
// what an error message has to name: the reader of that message is looking at the
// file in an editor, not at an array. A quoted field holding a newline therefore
// advances it by more than one, which is exactly right -- the next row genuinely
// does start further down the file than a naive count would say.
public readonly record struct CsvRow(int LineNumber, IReadOnlyList<string> Fields);

/// <summary>The file is not CSV at all, or its header is unusable. Not a row problem.</summary>
// The whole point of #62 is that per-row problems are reported rather than
// refused, so this type is deliberately narrow: it is thrown only for the things
// that make every row meaningless -- an empty file, a header missing a column, a
// quote that is never closed. Anything wrong with one row travels as an
// ImportRow.Reason instead and does not stop the other rows importing.
public sealed class CsvFormatException(string message) : Exception(message);

/// <summary>Splits RFC 4180 text into rows of fields. Knows nothing about transactions.</summary>
// Written by hand rather than taking CsvHelper, which is the standard answer and
// is better tested than this will ever be. It lost on CLAUDE.md's dependency rule
// against a scope that is genuinely small: the whole of RFC 4180 is quoted fields,
// doubled quotes inside them, and two line endings. That is the state machine
// below, and it is checked by tests that name each of those rules. The moment this
// has to read a dialect -- semicolons, an escape character, a fixed encoding per
// file -- CsvHelper is the right answer and this is the thing to delete.
public static class CsvReader
{
    /// <summary>Every row in the file, in order. Blank lines are not rows.</summary>
    // Returns a list rather than an IEnumerable with `yield return`, and the reason
    // is the exception above rather than performance. A deferred iterator throws
    // while it is being enumerated -- which here would be halfway through the
    // endpoint's loop, after some rows had already been turned into entities, with
    // the failure arriving from a foreach that looks like it only reads. Doing the
    // work up front means "this file is not CSV" is answered before anything acts
    // on it. The file is capped at a megabyte by the endpoint, so buffering it is
    // not a cost worth trading that for.
    public static IReadOnlyList<CsvRow> Read(string text)
    {
        var rows = new List<CsvRow>();
        var fields = new List<string>();
        var field = new StringBuilder();

        // Two counters, and they are not the same number the moment a quoted field
        // holds a newline: `line` is where the cursor is, `rowStartLine` is where
        // the row being built began, and the second is what gets reported.
        var line = 1;
        var rowStartLine = 1;

        var inQuotes = false;
        var atFieldStart = true;

        // Whether any field in this row was quoted, which is the only thing that
        // tells `""` on a line apart from a blank line. The first is a deliberate
        // empty value and is a row; the second is whitespace at the end of a bank
        // export and is not.
        var rowHadQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote is the escape for a literal one. Anything
                    // else after a quote closes the field -- including, leniently,
                    // a character that RFC 4180 says may not be there. Refusing it
                    // would turn a stray quote in one description into a file-level
                    // failure, which is the opposite of what this issue asks for.
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }

                    continue;
                }

                // Counted but not treated as a row ending: inside quotes a newline
                // is content. The line ending is kept verbatim rather than
                // normalised, because the field is the user's text and this reader
                // has no business editing it; TransactionCsv trims it afterwards.
                if (c == '\n')
                {
                    line++;
                }

                field.Append(c);
                continue;
            }

            switch (c)
            {
                // Only at the very start of a field. A quote in the middle of an
                // unquoted field -- 5" of rain -- is a literal, which is what the
                // default branch below makes it.
                case '"' when atFieldStart:
                    inQuotes = true;
                    rowHadQuotes = true;
                    atFieldStart = false;
                    continue;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    atFieldStart = true;
                    continue;

                case '\r' or '\n':
                    // CRLF is one ending, not two. A lone CR ends a row as well:
                    // it costs one condition and it means a file saved by a very
                    // old tool is read rather than seen as a single enormous line.
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    fields.Add(field.ToString());
                    field.Clear();
                    line++;

                    if (!IsBlank(fields, rowHadQuotes))
                    {
                        rows.Add(new CsvRow(rowStartLine, fields.ToArray()));
                    }

                    fields.Clear();
                    rowStartLine = line;
                    atFieldStart = true;
                    rowHadQuotes = false;
                    continue;

                default:
                    atFieldStart = false;
                    field.Append(c);
                    continue;
            }
        }

        if (inQuotes)
        {
            throw new CsvFormatException(
                $"The quoted field beginning on line {rowStartLine} is never closed. "
                + "A field may contain a quote by doubling it (\"\"), and the file must not end inside one.");
        }

        // The last row, when the file does not end with a newline -- which is most
        // files. The three conditions are the three ways a row can be in progress:
        // a field with text in it, a comma already seen, or a quoted field that
        // happened to be empty.
        if (field.Length > 0 || fields.Count > 0 || rowHadQuotes)
        {
            fields.Add(field.ToString());

            if (!IsBlank(fields, rowHadQuotes))
            {
                rows.Add(new CsvRow(rowStartLine, fields.ToArray()));
            }
        }

        return rows;
    }

    /// <summary>A line with nothing on it, which is not a row and not an error.</summary>
    // A row of `,,,` has four empty fields and is a real row that will be rejected
    // for its dates; a row of `""` was written deliberately and is also a row. Only
    // a line with no separators, no quotes and no text is skipped, which is the
    // trailing newline every editor adds and the blank line every bank export ends
    // with.
    private static bool IsBlank(List<string> fields, bool rowHadQuotes) =>
        !rowHadQuotes && fields.Count == 1 && fields[0].Length == 0;
}
