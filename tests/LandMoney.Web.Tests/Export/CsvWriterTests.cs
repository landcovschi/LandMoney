using System.Text;
using LandMoney.Web.Export;
using LandMoney.Web.Import;

namespace LandMoney.Web.Tests.Export;

/// <summary>#89. RFC 4180 on the way out, checked against the reader on the way in.</summary>
public class CsvWriterTests
{
    [Theory]
    [InlineData("linella")]
    [InlineData("groceries")]
    [InlineData("")]
    [InlineData("2026-06-02")]
    public void An_ordinary_field_is_written_as_it_is(string value) =>
        Assert.Equal(value, CsvWriter.Field(value));

    // The field that made a writer necessary rather than a string.Join. #62's
    // verification names `lidl, centru` as a real description, and unquoted it is
    // two fields -- so the eval set would gain a row with six columns and score.py
    // would refuse the whole file.
    [Fact]
    public void A_comma_is_quoted() =>
        Assert.Equal("\"lidl, centru\"", CsvWriter.Field("lidl, centru"));

    // Doubled, not backslash-escaped. CSV has no escape character, which is the
    // single most common way a hand-written writer produces a file its own reader
    // cannot read back.
    [Fact]
    public void A_quote_is_doubled_and_the_field_is_quoted() =>
        Assert.Equal("\"the \"\"green\"\" shop\"", CsvWriter.Field("the \"green\" shop"));

    [Theory]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("two\r\nlines", "\"two\r\nlines\"")]
    public void A_line_ending_inside_a_field_is_quoted(string value, string expected) =>
        Assert.Equal(expected, CsvWriter.Field(value));

    // Not required by RFC 4180 and done anyway, because evals/score.py reads the
    // file with Python's csv module, which does not trim. Without the quotes the
    // exported row and the transaction it came from would differ by a character
    // nobody can see.
    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("\ttab")]
    public void Surrounding_whitespace_is_quoted(string value) =>
        Assert.Equal($"\"{value}\"", CsvWriter.Field(value));

    // A space in the middle is not surrounding whitespace, and quoting it would
    // quote nearly every description in the file for nothing.
    [Fact]
    public void A_space_in_the_middle_is_not() =>
        Assert.Equal("bread and milk", CsvWriter.Field("bread and milk"));

    [Fact]
    public void A_line_is_the_fields_joined_and_terminated()
    {
        var builder = new StringBuilder();

        CsvWriter.AppendLine(builder, "a", "b", "c");

        Assert.Equal("a,b,c\r\n", builder.ToString());
    }

    // CRLF, and asserted rather than left to the constant, because it is the half of
    // the format a reader forgives and a diff does not: evals/transactions.csv is
    // CRLF in the working tree, and an appended block of LF rows is a change to
    // every line of the file the next time anything rewrites it.
    [Fact]
    public void The_line_ending_is_crlf() =>
        Assert.Equal("\r\n", CsvWriter.LineEnding);

    [Fact]
    public void An_empty_line_is_still_a_line()
    {
        var builder = new StringBuilder();

        CsvWriter.AppendLine(builder, "");

        Assert.Equal("\r\n", builder.ToString());
    }

    /// <summary>The assertion the other tests in this file are the detail of.</summary>
    // Written, then read back by the reader this application already ships, and
    // compared field by field. That is what makes "RFC 4180" a property of the pair
    // rather than a claim in a comment on each half -- and it is the test that would
    // catch a quoting rule that is self-consistent and wrong.
    [Theory]
    [InlineData("linella")]
    [InlineData("lidl, centru")]
    [InlineData("the \"green\" shop")]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    [InlineData("")]
    [InlineData("everything, \"at\" \r\nonce")]
    public void What_is_written_is_what_the_reader_reads(string description)
    {
        var builder = new StringBuilder();

        CsvWriter.AppendLine(builder, "occurred_at", "description");
        CsvWriter.AppendLine(builder, "2026-06-02", description);

        var rows = CsvReader.Read(builder.ToString());

        Assert.Equal(2, rows.Count);
        Assert.Equal(["2026-06-02", description], rows[1].Fields);
    }
}
