using LandMoney.Web.Api;
using LandMoney.Web.Auth;
using LandMoney.Web.Categorizing;
using LandMoney.Web.Data;
using LandMoney.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Tests.Categorizing;

/// <summary>#94. What the categorizer is shown, and what counts as a different question.</summary>
// Two callers depend on this and they depend on it from opposite ends. The edit
// endpoint asks "did I change the question", and answers by clearing a prediction
// and spending a model call; the sweep asks "is this still the question I asked",
// and answers by throwing away an answer it has already paid for. A disagreement
// between them is silent in both directions -- a stale category that looks right,
// or a row that owes a category nothing will ever collect.
//
// Nothing here opens a connection: the equality half is a record, and the SQL half
// is ToQueryString, which builds the command EF would send without a server. That
// is #22's property, defended the way PendingCategorizationTests defends it.
public class CategorizerQuestionTests
{
    // --- what the question is built from -------------------------------------

    [Fact]
    public void The_question_is_the_three_fields_a_predictor_is_shown()
    {
        var question = CategorizerQuestion.About(Row());

        Assert.Equal("linella", question.Description);
        Assert.Equal(42.50m, question.Amount);
        Assert.Equal("EUR", question.Currency);
    }

    // **The one that decides whether correcting a mistyped year costs money.**
    // CategorySuggestionRequest carries no date for the reason written on it -- a
    // day tells a predictor nothing -- so a date correction must not re-queue the
    // row. If this ever fails, either a date has been added to the question or
    // somebody has added the field here; both are fine, and both mean the edit
    // endpoint now spends 0.62 US cents on fixing a typo in a year.
    [Fact]
    public void The_day_the_money_was_spent_is_not_part_of_the_question()
    {
        var january = Row(occurredAt: new DateOnly(2026, 1, 4));
        var september = Row(occurredAt: new DateOnly(2026, 9, 1));

        Assert.Equal(CategorizerQuestion.About(january), CategorizerQuestion.About(september));
    }

    // Nothing else on the row is either. A category, a source and an attempt count
    // are answers and bookkeeping, and a row that gained one is not a row anybody
    // needs to ask about differently.
    [Fact]
    public void Nothing_the_categorizer_wrote_is_part_of_the_question()
    {
        var before = Row();
        var after = Row();
        after.Category = "groceries";
        after.CategorySource = CategorySources.Rules;
        after.CategorizationAttempts = 3;

        Assert.Equal(CategorizerQuestion.About(before), CategorizerQuestion.About(after));
    }

    // --- what counts as a change ---------------------------------------------

    [Fact]
    public void The_same_three_values_are_the_same_question() =>
        Assert.Equal(Question(), Question());

    [Theory]
    [InlineData("linella centru", 42.50, "EUR")]
    [InlineData("linella", 43.50, "EUR")]
    [InlineData("linella", 42.50, "MDL")]
    public void Changing_any_of_the_three_is_a_different_question(
        string description, decimal amount, string currency)
    {
        Assert.NotEqual(Question(), new CategorizerQuestion(description, amount, currency));
    }

    // **What makes an edit that changed nothing cost nothing**, and the reason it
    // is not obvious. Postgres stores this column as numeric(18,2), so a row saved
    // as 78.5 is read back as 78.50 -- different bit patterns, equal values. A
    // record's generated equality goes through EqualityComparer<decimal>.Default,
    // which is decimal.Equals, which compares values.
    //
    // If this ever answers "different", every edit form opened and saved with
    // nothing touched would clear the row's category and buy it again. #62's
    // TransactionKey depends on the same property from the other side, where
    // getting it wrong would be a silent double import.
    [Fact]
    public void The_scale_a_number_is_written_with_is_not_a_change()
    {
        Assert.Equal(
            new CategorizerQuestion("linella", 78.5m, "EUR"),
            new CategorizerQuestion("linella", 78.50m, "EUR"));
    }

    // **This is why the endpoint uppercases before it compares, and the test exists
    // to make that line un-deletable.** The comparison is ordinal, so "eur" and
    // "EUR" are two questions -- correct, because the categorizer really is shown
    // the string and #65 keys its cache on it. What must not happen is a client
    // sending "eur" for a row stored as "EUR" and paying for an answer to a
    // question nothing changed about.
    [Fact]
    public void The_currency_is_compared_exactly_which_is_what_the_handler_normalises_for()
    {
        Assert.NotEqual(
            new CategorizerQuestion("linella", 42.50m, "EUR"),
            new CategorizerQuestion("linella", 42.50m, "eur"));
    }

    // The description is *not* normalised anywhere, on either side of the wire.
    // #59 records that the typed string reaches the model verbatim and that tidying
    // it in one place would improve a predictor and silently move the baseline it
    // is measured against. So any byte of difference is a different question, and
    // fixing the capital letter in a shop's name really does ask again.
    [Theory]
    [InlineData("Linella")]
    [InlineData("linella ")]
    [InlineData("linella  centru")]
    public void Any_difference_in_the_description_is_a_different_question(string description)
    {
        Assert.NotEqual(Question(), Question(description: description));
    }

    // --- what happens to it on the way to Postgres ---------------------------

    // The guard #94 added to the sweep's UPDATE. A batch is the better part of a
    // minute against a model, so a row can be edited between the question and the
    // answer -- and without this clause the answer is written anyway, describing
    // text that is no longer in the row and looking entirely plausible.
    [Fact]
    public void The_three_fields_all_reach_the_WHERE_clause()
    {
        var sql = Where(Query());

        Assert.Contains("description", sql, StringComparison.Ordinal);
        Assert.Contains("amount", sql, StringComparison.Ordinal);
        Assert.Contains("currency", sql, StringComparison.Ordinal);
    }

    // An Expression and never a Func: a compiled delegate is something EF cannot
    // translate, so the provider would fetch the table and filter it in memory --
    // silently, correctly, and unusably. This says the comparison was translated
    // rather than deferred, by looking for the parameters it must have produced.
    [Fact]
    public void The_values_are_parameters_rather_than_a_filter_run_in_memory()
    {
        Assert.Contains("@", Where(Query()), StringComparison.Ordinal);
    }

    // --- helpers -------------------------------------------------------------

    private static CategorizerQuestion Question(
        string description = "linella", decimal amount = 42.50m, string currency = "EUR") =>
        new(description, amount, currency);

    private static Transaction Row(DateOnly? occurredAt = null) => new()
    {
        Currency = "EUR",
        Description = "linella",
        Amount = 42.50m,
        OccurredAt = occurredAt ?? new DateOnly(2026, 9, 1),
    };

    private static string Query()
    {
        using var db = Context();

        return db.Transactions
            .IgnoreQueryFilters()
            .Where(Question().Unchanged())
            .ToQueryString();
    }

    // The WHERE clause alone. Every one of these three is also a column of the
    // entity and therefore appears in the SELECT list whatever the filter does,
    // which is the trap PendingCategorizationTests records having walked into.
    private static string Where(string sql)
    {
        var start = sql.IndexOf("WHERE", StringComparison.Ordinal);

        return start < 0 ? string.Empty : sql[start..];
    }

    // A connection string that parses and is never opened -- the shape and the
    // reason are OwnershipFilterTests'.
    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=test-only;Database=none;Username=none;Password=none")
                .UseSnakeCaseNamingConvention()
                .Options,
            new NobodySignedIn());

    private sealed class NobodySignedIn : ICurrentUser
    {
        public string? OwnerId => null;
    }
}
