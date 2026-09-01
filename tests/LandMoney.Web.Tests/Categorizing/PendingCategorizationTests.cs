using LandMoney.Web.Api;
using LandMoney.Web.Auth;
using LandMoney.Web.Categorizing;
using LandMoney.Web.Data;
using LandMoney.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Tests.Categorizing;

/// <summary>Which rows #92's sweep is allowed to touch, and which it must not.</summary>
// The expression is compiled and run in memory for the rules, and translated to SQL
// for the one condition whose meaning changes on the way there. Both halves are
// needed and neither covers the other: C# and SQL disagree about null, and this
// predicate is three conditions of which two involve one.
//
// Nothing here opens a connection, which is the property #22 defends and #52
// records: ToQueryString builds the command EF would send without a server.
public class PendingCategorizationTests
{
    private const int Cap = 30;

    // --- what the marker means -----------------------------------------------

    [Fact]
    public void A_row_that_was_just_created_is_owed_a_category()
    {
        Assert.True(Owed(Row(attempts: PendingCategorization.Owing)));
    }

    // Added after a mutation sweep: `Owing = 1` survived every other test here,
    // because a row entering with one attempt already spent is still owed a
    // category and still stops at the cap. What it silently does is shorten every
    // row's budget by one, which is a tuning change nobody made and nothing
    // reports. The count means "attempts charged so far", and a row nothing has
    // asked yet has had none.
    [Fact]
    public void A_row_enters_the_queue_having_spent_nothing()
    {
        Assert.Equal(0, PendingCategorization.Owing);
    }

    // The reason the column exists at all, and the reason it is not
    // `category IS NULL`.
    //
    // #63 records that clearing a category in the interface writes null to both
    // category columns, so a row somebody deliberately cleared looks exactly like
    // one nothing has ever touched -- and it says to reopen the question "the day
    // something re-categorises existing rows". #92 is that day. A cleared row was
    // never marked as owing anything, so it is not owed anything, and the sweep
    // cannot re-predict over a person's "I do not know either".
    //
    // The same test covers every row written before this column existed, for the
    // same reason and with the same answer.
    [Fact]
    public void A_row_that_owes_nothing_is_never_swept()
    {
        Assert.False(Owed(Row(attempts: null)));
    }

    [Fact]
    public void A_row_below_the_cap_is_still_owed_a_category()
    {
        Assert.True(Owed(Row(attempts: Cap - 1)));
    }

    // #92's fourth trap: a sweep that retries for ever is a bill with no ceiling
    // once the model is on, at about 0.62 US cents an attempt.
    [Fact]
    public void A_row_that_has_used_up_its_attempts_is_given_up_on()
    {
        Assert.False(Owed(Row(attempts: Cap)));
    }

    // Above rather than at, because an overlapping sweep across a revision can
    // increment past the boundary and `== Cap` would let it back in for ever.
    [Fact]
    public void A_row_past_the_cap_stays_given_up_on()
    {
        Assert.False(Owed(Row(attempts: Cap + 7)));
    }

    // The count is kept rather than cleared when the cap is reached, so that
    // "tried and gave up" is a state somebody can find. If this ever starts
    // answering true, an abandoned row has been made indistinguishable from a row
    // nobody owed anything about.
    [Fact]
    public void Giving_up_is_recorded_rather_than_erased()
    {
        var abandoned = Row(attempts: Cap);

        Assert.False(Owed(abandoned));
        Assert.NotNull(abandoned.CategorizationAttempts);
    }

    // --- the never-overwrite rule --------------------------------------------

    // #92's second trap, and the first caller where CategorySources.MayOverwrite is
    // not trivially true. In CreateAsync the transaction was constructed thirty
    // lines above and had no source at all; here the row has been sitting in a
    // database and may have been labelled by a person in the meantime.
    [Fact]
    public void A_row_a_person_labelled_is_never_re_predicted()
    {
        Assert.False(Owed(Row(attempts: PendingCategorization.Owing, source: CategorySources.Human)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CategorySources.Rules)]
    [InlineData(CategorySources.Model)]
    public void A_row_nothing_has_claimed_may_be_predicted(string? source)
    {
        Assert.True(Owed(Row(attempts: PendingCategorization.Owing, source: source)));
    }

    // The two spellings of one rule, pinned to each other. MayOverwrite is a method
    // and EF cannot translate a method call into SQL, so the predicate spells the
    // comparison out instead -- which is a copy, and a copy is a thing that drifts.
    // This is the same answer CategoriesTests gives for the vocabulary: not a
    // shared implementation, but a test that fails if the two stop agreeing.
    [Theory]
    [InlineData(null)]
    [InlineData(CategorySources.Rules)]
    [InlineData(CategorySources.Model)]
    [InlineData(CategorySources.Human)]
    [InlineData("something-else")]
    public void The_predicate_says_what_MayOverwrite_says(string? source)
    {
        Assert.Equal(
            CategorySources.MayOverwrite(source),
            Owed(Row(attempts: PendingCategorization.Owing, source: source)));
    }

    // --- what happens to it on the way to Postgres ---------------------------

    // The condition that changes meaning in translation, and the reason this file
    // is not only a set of in-memory assertions.
    //
    // In C#, `source != "human"` is true when source is null. In SQL,
    // `category_source <> 'human'` is *unknown* when the column is null, and
    // unknown does not pass a WHERE clause -- so a literal translation would
    // exclude exactly the rows this sweep exists for: the ones nothing has
    // categorised. EF Core's null semantics rewrite it, and this asserts the
    // rewrite happened rather than trusting the paragraph that says it does.
    //
    // Same class of trap as #88's leading slash: the behaviour differs between
    // where the code was written and where it runs, and only one of the two is
    // exercised by reading it.
    [Fact]
    public void The_null_source_survives_the_trip_into_SQL()
    {
        var sql = Query();

        Assert.Contains("category_source IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_marker_and_the_cap_both_reach_the_WHERE_clause()
    {
        var sql = Query();

        Assert.Contains("categorization_attempts IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("categorization_attempts", sql, StringComparison.Ordinal);
        Assert.Contains(Cap.ToString(), sql, StringComparison.Ordinal);
    }

    // The sweep runs outside a request, so there is no signed-in user and the
    // global filter -- `owner_id = @current` -- would match nothing at all, for
    // ever, looking exactly like a categorizer that was never reached. #92's third
    // trap. This asserts the escape hatch is what makes the query reach any rows;
    // OwnershipFilterTests holds the other half, that it is the only way past.
    [Fact]
    public void The_sweep_has_to_ignore_the_owner_filter_to_see_anything()
    {
        using var db = Context();

        var filtered = db.Transactions.Where(PendingCategorization.Owed(Cap)).ToQueryString();

        Assert.Contains("owner_id", Where(filtered), StringComparison.Ordinal);
        Assert.DoesNotContain("owner_id", Where(Query()), StringComparison.Ordinal);
    }

    // --- helpers -------------------------------------------------------------

    private static bool Owed(Transaction transaction) =>
        PendingCategorization.Owed(Cap).Compile()(transaction);

    private static Transaction Row(int? attempts, string? source = null) => new()
    {
        Currency = "EUR",
        Description = "linella",
        Amount = 42.50m,
        OccurredAt = new DateOnly(2026, 9, 1),
        CategorizationAttempts = attempts,
        CategorySource = source,
        Category = source is null ? null : "groceries",
    };

    // The WHERE clause alone. `owner_id` is a column of the entity, so it appears
    // in every SELECT list whether the filter applies or not, and asserting over
    // the whole statement finds it either way -- which is the same trap
    // OwnershipFilterTests records for the parameter value, met from the other
    // side. This test failed on exactly that before it was narrowed.
    private static string Where(string sql)
    {
        var start = sql.IndexOf("WHERE", StringComparison.Ordinal);

        return start < 0 ? string.Empty : sql[start..];
    }

    private static string Query()
    {
        using var db = Context();

        return db.Transactions
            .IgnoreQueryFilters()
            .Where(PendingCategorization.Owed(Cap))
            .ToQueryString();
    }

    // A connection string that parses and is never opened -- OwnershipFilterTests
    // established the shape and the reason.
    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=test-only;Database=none;Username=none;Password=none")
                .UseSnakeCaseNamingConvention()
                .Options,
            new NobodySignedIn());

    // Which is what a background service actually gets: CurrentUser reads
    // IHttpContextAccessor, and there is no HttpContext outside a request.
    private sealed class NobodySignedIn : ICurrentUser
    {
        public string? OwnerId => null;
    }
}
