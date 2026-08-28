using System.ComponentModel.DataAnnotations;
using LandMoney.Web.Api;
using LandMoney.Web.Import;
using LandMoney.Web.Tests.Api;
using Microsoft.Extensions.DependencyInjection;

namespace LandMoney.Web.Tests.Import;

/// <summary>Reading the four columns, and refusing what cannot be read.</summary>
public class TransactionCsvTests
{
    private const string Header = "occurred_at,amount,currency,description";

    /// <summary>A day the fixed clock below calls plausible, used wherever the date is not the subject.</summary>
    private const string AnyDay = "2026-06-02";

    private static ParsedFile Parse(params string[] lines) =>
        TransactionCsv.Parse(string.Join("\n", lines));

    private static ImportRow OneRow(params string[] lines) =>
        Assert.Single(Parse([Header, .. lines]).Rows);

    [Fact]
    public void It_reads_the_four_columns()
    {
        var row = OneRow($"{AnyDay},412.50,MDL,linella");

        var request = Assert.IsType<CreateTransactionRequest>(row.Request);
        Assert.Equal(new DateOnly(2026, 6, 2), request.OccurredAt);
        Assert.Equal(412.50m, request.Amount);
        Assert.Equal("MDL", request.Currency);
        Assert.Equal("linella", request.Description);
        Assert.Null(row.Reason);
    }

    [Fact]
    public void The_columns_may_be_in_any_order()
    {
        var parsed = Parse(
            "description,currency,amount,occurred_at",
            $"linella,MDL,412.50,{AnyDay}");

        var request = Assert.IsType<CreateTransactionRequest>(Assert.Single(parsed.Rows).Request);
        Assert.Equal("linella", request.Description);
        Assert.Equal(412.50m, request.Amount);
    }

    [Fact]
    public void The_header_may_be_title_cased_or_padded()
    {
        var parsed = Parse(
            " Occurred_At , Amount , Currency , Description ",
            $"{AnyDay},412.50,MDL,linella");

        Assert.NotNull(Assert.Single(parsed.Rows).Request);
    }

    [Fact]
    public void A_missing_column_refuses_the_whole_file_and_names_it()
    {
        var exception = Assert.Throws<CsvFormatException>(
            () => Parse("occurred_at,amount,description", $"{AnyDay},412.50,linella"));

        Assert.Contains("currency", exception.Message);
        Assert.Contains(TransactionCsv.HeaderExample, exception.Message);
    }

    [Fact]
    public void A_column_named_twice_refuses_the_whole_file()
    {
        var exception = Assert.Throws<CsvFormatException>(
            () => Parse($"{Header},amount", $"{AnyDay},412.50,MDL,linella,99"));

        Assert.Contains("amount", exception.Message);
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        var exception = Assert.Throws<CsvFormatException>(() => TransactionCsv.Parse(string.Empty));

        Assert.Contains(TransactionCsv.HeaderExample, exception.Message);
    }

    // evals/transactions.csv carries a fifth `category` column, and reading that
    // file is the point of the whole feature -- so an extra column can neither
    // refuse the file nor disappear without a word.
    [Fact]
    public void An_extra_column_is_reported_and_the_rows_are_still_read()
    {
        var parsed = Parse(
            $"{Header},category",
            $"{AnyDay},412.50,MDL,linella,groceries");

        Assert.Equal(["category"], parsed.IgnoredColumns);
        Assert.NotNull(Assert.Single(parsed.Rows).Request);
    }

    // The decision recorded for #62: a file written with a comma for the decimal
    // point is not supported, and it fails by name rather than by being read as a
    // number one hundred times too large. Quoted, because a bare comma is a field
    // separator and would never reach the number parser -- see the test below,
    // which is the more likely way a Romanian export actually arrives.
    //
    // **This is the test that dies if NumberStyles.Number is ever written in place
    // of the two flags**, because that constant includes AllowThousands and would
    // read "1,50" as one hundred and fifty without a word.
    [Theory]
    [InlineData("\"1,50\"")]
    [InlineData("\"1.234,56\"")]
    [InlineData("\"1,234.56\"")]
    [InlineData("1 234.56")]
    [InlineData("412.50 MDL")]
    [InlineData("")]
    public void An_amount_that_is_not_written_the_invariant_way_is_rejected(string amount)
    {
        var row = OneRow($"{AnyDay},{amount},MDL,linella");

        Assert.Null(row.Request);
        Assert.Contains("1234.56", row.Reason);
    }

    // What a Romanian export really does when nothing quotes its amounts: the comma
    // is a separator, so the row simply has one field too many. Refused either way,
    // which is the decision -- but the message names the field count rather than the
    // number, and that is worth knowing before somebody reads the message and goes
    // looking for a typo in a value that is not the problem.
    [Fact]
    public void An_unquoted_comma_decimal_is_rejected_for_its_field_count()
    {
        var row = OneRow($"{AnyDay},1,50,MDL,linella");

        Assert.Null(row.Request);
        Assert.Contains("5 fields", row.Reason);
    }

    [Fact]
    public void An_invariant_amount_is_read_exactly()
    {
        var row = OneRow($"{AnyDay},1234.56,MDL,linella");

        Assert.Equal(1234.56m, Assert.IsType<CreateTransactionRequest>(row.Request).Amount);
    }

    // Parsed rather than refused here, so that [Range] is what answers -- and the
    // message then names the amount rule instead of saying "not a number", which
    // would send somebody looking for a typo in a field that is perfectly readable.
    [Fact]
    public void A_negative_amount_is_read_here_and_refused_by_validation()
    {
        var row = OneRow($"{AnyDay},-412.50,MDL,linella");

        var request = Assert.IsType<CreateTransactionRequest>(row.Request);
        Assert.Equal(-412.50m, request.Amount);

        var messages = Validate(request);
        Assert.Contains(messages, message => message.Contains("Amount"));
    }

    [Fact]
    public void A_third_decimal_place_is_read_here_and_refused_by_validation()
    {
        var row = OneRow($"{AnyDay},12.345,MDL,linella");

        Assert.NotEmpty(Validate(Assert.IsType<CreateTransactionRequest>(row.Request)));
    }

    // #62: "OccurredAt is a day, not an instant. A CSV column holding a timestamp
    // has to be truncated deliberately, not converted." Refusing is the most
    // deliberate form of that available -- the file states no zone, so there is no
    // correct day to derive.
    [Theory]
    [InlineData("2026-06-02T14:33:00")]
    [InlineData("02/06/2026")]
    [InlineData("2026-6-2")]
    [InlineData("06/02/2026")]
    public void A_date_that_is_not_yyyy_MM_dd_is_rejected(string date)
    {
        var row = OneRow($"{date},412.50,MDL,linella");

        Assert.Null(row.Request);
        Assert.Contains(TransactionCsv.DateFormat, row.Reason);
    }

    [Fact]
    public void A_row_with_the_wrong_number_of_fields_is_rejected()
    {
        var row = OneRow($"{AnyDay},412.50,MDL,lidl, centru");

        Assert.Null(row.Request);
        Assert.Contains("5 fields", row.Reason);
        Assert.Contains("quotes", row.Reason);
    }

    [Fact]
    public void A_rejected_row_carries_the_line_it_is_on()
    {
        var parsed = Parse(
            Header,
            $"{AnyDay},412.50,MDL,linella",
            "not-a-date,412.50,MDL,linella");

        Assert.Equal(3, parsed.Rows[1].LineNumber);
    }

    // One bad row imports the rest and names the bad one -- #62's second acceptance
    // test, at the level where it is decided.
    [Fact]
    public void One_bad_row_does_not_stop_the_others()
    {
        var parsed = Parse(
            Header,
            $"{AnyDay},412.50,MDL,linella",
            "not-a-date,78.00,MDL,shaorma",
            $"{AnyDay},9.99,EUR,netflix");

        Assert.Equal(3, parsed.Rows.Count);
        Assert.NotNull(parsed.Rows[0].Request);
        Assert.Null(parsed.Rows[1].Request);
        Assert.NotNull(parsed.Rows[2].Request);
    }

    [Fact]
    public void Both_problems_in_one_row_are_reported_together()
    {
        var row = OneRow("not-a-date,1x50,MDL,linella");

        Assert.Null(row.Request);
        Assert.Contains(TransactionCsv.DateFormat, row.Reason);
        Assert.Contains("1234.56", row.Reason);
    }

    // Trimmed, so a spreadsheet's padding does not become part of a description the
    // categorizer then fails to match. Not upper-cased: that happens in the endpoint,
    // in the same line CreateAsync writes, so there is one such line per path.
    [Fact]
    public void Values_are_trimmed_but_the_currency_is_not_upper_cased_here()
    {
        var row = OneRow($"{AnyDay},412.50, mdl , linella ");

        var request = Assert.IsType<CreateTransactionRequest>(row.Request);
        Assert.Equal("mdl", request.Currency);
        Assert.Equal("linella", request.Description);
    }

    [Fact]
    public void A_quoted_description_holding_a_comma_is_one_field()
    {
        var row = OneRow($"{AnyDay},412.50,MDL,\"lidl, centru\"");

        Assert.Equal("lidl, centru", Assert.IsType<CreateTransactionRequest>(row.Request).Description);
    }

    [Fact]
    public void A_file_with_only_a_header_reads_as_no_rows()
    {
        Assert.Empty(Parse(Header).Rows);
    }

    /// <summary>The endpoint's validation step, run here so a parsed row's rules can be asserted.</summary>
    // The same two arguments TransactionEndpoints.ImportAsync passes, which is the
    // claim being checked: a row read out of a CSV is judged by exactly the rules a
    // row posted as JSON is judged by. The clock is fixed so the dates in this file
    // stay plausible after 2031 -- MaxYearsBehind is five years, and a test that
    // starts failing on a calendar date is a test nobody trusts.
    private static IReadOnlyList<string> Validate(CreateTransactionRequest request)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(FixedTimeProvider.At(new DateOnly(2026, 6, 15)));

        var results = new List<ValidationResult>();

        Validator.TryValidateObject(
            request,
            new ValidationContext(request, services.BuildServiceProvider(), items: null),
            results,
            validateAllProperties: true);

        return results.Select(result => result.ErrorMessage ?? string.Empty).ToArray();
    }
}
