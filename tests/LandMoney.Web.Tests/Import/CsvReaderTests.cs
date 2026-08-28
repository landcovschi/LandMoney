using LandMoney.Web.Import;

namespace LandMoney.Web.Tests.Import;

/// <summary>The whole of RFC 4180, one rule per test.</summary>
// CsvReader was written by hand instead of taking CsvHelper, and this file is the
// price of that decision -- the argument in CsvReader's own comment is only
// honest if each of the rules it claims to implement is named here. Every test
// below corresponds to a sentence in that comment.
public class CsvReaderTests
{
    [Fact]
    public void A_plain_row_is_split_on_commas()
    {
        var rows = CsvReader.Read("a,b,c");

        var row = Assert.Single(rows);
        Assert.Equal(["a", "b", "c"], row.Fields);
    }

    [Fact]
    public void A_quoted_field_may_hold_a_comma()
    {
        var rows = CsvReader.Read("""2026-06-02,"lidl, centru",MDL""");

        Assert.Equal(["2026-06-02", "lidl, centru", "MDL"], rows[0].Fields);
    }

    [Fact]
    public void A_doubled_quote_inside_a_quoted_field_is_one_quote()
    {
        var rows = CsvReader.Read("""a,"5"" of rain",c""");

        Assert.Equal(["a", "5\" of rain", "c"], rows[0].Fields);
    }

    // The lenient half of the reader, and it is lenient on purpose: a stray quote
    // in one description must not become a file-level failure.
    [Fact]
    public void A_quote_that_does_not_start_a_field_is_a_literal()
    {
        var rows = CsvReader.Read("""a,5" of rain,c""");

        Assert.Equal(["a", "5\" of rain", "c"], rows[0].Fields);
    }

    [Fact]
    public void A_quoted_field_may_hold_a_newline()
    {
        var rows = CsvReader.Read("a,\"two\nlines\",c\nd,e,f");

        Assert.Equal(["a", "two\nlines", "c"], rows[0].Fields);
        Assert.Equal(["d", "e", "f"], rows[1].Fields);
    }

    // The reason LineNumber exists rather than the row's index: the second row
    // starts on the *third* line of the file, and that is the number somebody
    // looking at the file in an editor needs to be told.
    [Fact]
    public void A_newline_inside_a_quoted_field_advances_the_line_number()
    {
        var rows = CsvReader.Read("a,\"two\nlines\",c\nd,e,f");

        Assert.Equal(1, rows[0].LineNumber);
        Assert.Equal(3, rows[1].LineNumber);
    }

    [Fact]
    public void Crlf_and_lf_produce_the_same_rows()
    {
        var withLf = CsvReader.Read("a,b\nc,d");
        var withCrlf = CsvReader.Read("a,b\r\nc,d");

        Assert.Equal(withLf.Select(row => row.Fields), withCrlf.Select(row => row.Fields));
        Assert.Equal(withLf.Select(row => row.LineNumber), withCrlf.Select(row => row.LineNumber));
    }

    [Fact]
    public void A_lone_carriage_return_also_ends_a_row()
    {
        var rows = CsvReader.Read("a,b\rc,d");

        Assert.Equal(2, rows.Count);
    }

    // Every editor writes one, so this is the commonest input there is. Without the
    // blank-row rule it would produce a phantom final row that the header check
    // then reports as having the wrong number of fields.
    [Fact]
    public void A_trailing_newline_does_not_produce_an_empty_row()
    {
        var rows = CsvReader.Read("a,b\nc,d\n");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void A_blank_line_between_rows_is_skipped()
    {
        var rows = CsvReader.Read("a,b\n\nc,d\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].LineNumber);

        // Skipped, but still counted: the second row is on line 3 of the file.
        Assert.Equal(3, rows[1].LineNumber);
    }

    // The one thing that tells a deliberate empty value apart from a blank line,
    // and the reason CsvReader tracks whether a row was quoted at all.
    [Fact]
    public void A_quoted_empty_field_on_its_own_line_is_a_row()
    {
        var rows = CsvReader.Read("\"\"\n");

        var row = Assert.Single(rows);
        Assert.Equal([""], row.Fields);
    }

    [Fact]
    public void A_row_of_only_separators_is_a_row_of_empty_fields()
    {
        var rows = CsvReader.Read(",,,");

        var row = Assert.Single(rows);
        Assert.Equal(["", "", "", ""], row.Fields);
    }

    [Fact]
    public void An_empty_file_has_no_rows()
    {
        Assert.Empty(CsvReader.Read(string.Empty));
    }

    [Fact]
    public void A_file_ending_inside_a_quoted_field_is_refused()
    {
        var exception = Assert.Throws<CsvFormatException>(
            () => CsvReader.Read("a,b\nc,\"never closed"));

        // The message names the line the quote opened on, not the end of the file:
        // that is where the person reading it has to go and look.
        Assert.Contains("line 2", exception.Message);
    }

    // Deferred execution would make the throw above arrive during the endpoint's
    // foreach, after some rows had already been turned into entities. This asserts
    // the reader does its work when it is called.
    [Fact]
    public void The_whole_file_is_read_before_anything_is_returned()
    {
        Assert.Throws<CsvFormatException>(() => CsvReader.Read("a,b\nc,\"never closed"));
    }
}
