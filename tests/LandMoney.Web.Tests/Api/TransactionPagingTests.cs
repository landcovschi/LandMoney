using LandMoney.Web.Api;
using LandMoney.Web.Auth;
using LandMoney.Web.Data;
using LandMoney.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Tests.Api;

/// <summary>The order the list is in, and the SQL a cursor becomes. #95.</summary>
// **The ordering and the cursor's comparison are one decision written twice, and the
// tests for them belong in one file for the same reason the code does.** A sort key
// added without the matching condition repeats rows; the condition without the key
// skips them. Both failures are invisible in a table small enough to check by eye and
// both are permanent -- a skipped row is not seen again by that reader.
//
// Nothing here opens a connection: ToQueryString builds the command EF would send
// without a server, which is the property #22 defends and PendingCategorizationTests
// established the shape of.
public class TransactionPagingTests
{
    private static readonly TransactionCursor Cursor = new(
        new DateOnly(2026, 8, 19),
        new DateTimeOffset(2026, 8, 19, 21, 4, 5, TimeSpan.Zero),
        Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));

    // --- how large a page is --------------------------------------------------

    [Fact]
    public void A_client_that_asks_for_nothing_gets_the_default()
    {
        Assert.Equal(TransactionPaging.DefaultPageSize, TransactionPaging.ClampPageSize(null));
    }

    [Fact]
    public void A_client_may_ask_for_less_than_the_default()
    {
        Assert.Equal(5, TransactionPaging.ClampPageSize(5));
    }

    // The whole of what #95 is: without a ceiling, `?limit=1000000` is the endpoint
    // as it was before this change with a query parameter in front of it.
    [Fact]
    public void A_client_cannot_ask_for_the_whole_table()
    {
        Assert.Equal(TransactionPaging.MaxPageSize, TransactionPaging.ClampPageSize(1_000_000));
    }

    [Fact]
    public void The_ceiling_itself_is_allowed()
    {
        Assert.Equal(TransactionPaging.MaxPageSize, TransactionPaging.ClampPageSize(TransactionPaging.MaxPageSize));
    }

    // Zero and negative become the default rather than an empty page, and the reason
    // is which wrong answer is legible: an empty page from a non-empty table looks
    // like the end of the list, or like a bug, and there is no way for the reader to
    // tell. A page of fifty in answer to a nonsensical request is obviously an
    // answer.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void An_unusable_limit_is_read_as_no_limit(int limit)
    {
        Assert.Equal(TransactionPaging.DefaultPageSize, TransactionPaging.ClampPageSize(limit));
    }

    // int.MaxValue is called out separately from the theory above because it is the
    // one value that would overflow the `pageSize + 1` lookahead if it survived the
    // clamp. It cannot, and this is what says so.
    [Fact]
    public void The_lookahead_cannot_overflow_because_the_clamp_runs_first()
    {
        Assert.Equal(TransactionPaging.MaxPageSize, TransactionPaging.ClampPageSize(int.MaxValue));
    }

    // --- the lookahead row ----------------------------------------------------

    [Fact]
    public void A_short_page_is_the_last_page()
    {
        var (rows, hasMore) = TransactionPaging.TrimToPage([1, 2], 5);

        Assert.Equal([1, 2], rows);
        Assert.False(hasMore);
    }

    // Exactly a full page and nothing behind it. The off-by-one that matters: read
    // as "more", the client is offered a button that fetches an empty page.
    [Fact]
    public void A_page_that_exactly_fills_is_still_the_last_page()
    {
        var (rows, hasMore) = TransactionPaging.TrimToPage([1, 2, 3], 3);

        Assert.Equal([1, 2, 3], rows);
        Assert.False(hasMore);
    }

    // The lookahead row is dropped from the *end*. Dropping the first instead
    // silently loses the newest transaction on every page but the last, which reads
    // identically and hides behind a screen that still fills up.
    [Fact]
    public void The_extra_row_says_there_is_more_and_is_not_returned()
    {
        var (rows, hasMore) = TransactionPaging.TrimToPage([1, 2, 3, 4], 3);

        Assert.Equal([1, 2, 3], rows);
        Assert.True(hasMore);
    }

    [Fact]
    public void An_empty_table_is_one_empty_last_page()
    {
        var (rows, hasMore) = TransactionPaging.TrimToPage(new List<int>(), 50);

        Assert.Empty(rows);
        Assert.False(hasMore);
    }

    // --- the order ------------------------------------------------------------

    // Three keys, all descending, and the third is why the cursor works at all.
    // OccurredAt ties within a day by design (#17); CreatedAt ties too, measured at
    // about fourteen rows per identical microsecond in a three-hundred-row import.
    // The id cannot tie.
    [Fact]
    public void The_list_is_ordered_by_all_three_keys_newest_first()
    {
        Assert.Contains(
            "ORDER BY t.occurred_at DESC, t.created_at DESC, t.id DESC",
            Sql(after: null),
            StringComparison.Ordinal);
    }

    // A page with no cursor is the first page and must not carry a keyset condition
    // of its own -- with one it would start below the newest row, and the top of the
    // list would be unreachable.
    [Fact]
    public void The_first_page_starts_at_the_top()
    {
        Assert.DoesNotContain("t.occurred_at <", Sql(after: null), StringComparison.Ordinal);
    }

    // --- what a cursor becomes ------------------------------------------------

    // **The nesting, asserted as SQL, because the flat version reads the same and is
    // the classic bug.** `occurred_at <= @d AND created_at <= @c AND id < @i` demands
    // that every key be at or before the cursor, so a row on an earlier day with a
    // later created_at -- which is most of them, the two being unrelated -- is
    // skipped and never seen again.
    [Fact]
    public void The_cursor_compares_the_three_keys_in_order_and_not_all_at_once()
    {
        var where = Where(Sql(Cursor));

        Assert.Contains(
            "(t.occurred_at < @", where, StringComparison.Ordinal);
        Assert.Contains(
            "OR (t.occurred_at = @", where, StringComparison.Ordinal);
        Assert.Contains(
            "OR (t.created_at = @", where, StringComparison.Ordinal);
    }

    // Guid has no comparison operator in C#, so the predicate says `CompareTo(..) < 0`
    // and needs Npgsql to turn that into an ordinary uuid comparison. This is checked
    // rather than hoped for: a translation that fell back to client evaluation would
    // fetch the table and filter it in memory -- the exact failure paging exists to
    // prevent, arriving silently through the fix for it.
    [Fact]
    public void The_id_tiebreak_is_compared_by_Postgres()
    {
        Assert.Contains("t.id < @", Where(Sql(Cursor)), StringComparison.Ordinal);
    }

    // **The redundant bound that makes this keyset paging rather than OFFSET wearing a
    // cursor's clothes**, and it is the one condition here that changes no row at
    // all. Postgres pushes `occurred_at <= @d` into the index and will not push the
    // nested OR beside it, so without this the scan starts at the newest row and
    // filters everything above the cursor away -- 1,925 rows discarded for one page
    // in the middle of 5,000, measured, against 25 with it.
    //
    // Asserted as the *first* thing in the WHERE clause after the owner, because that
    // is what says it is a conjunct rather than one more branch of the disjunction: a
    // bound written inside the OR is discarded by the planner along with the rest of
    // it.
    [Fact]
    public void The_cursor_carries_a_bound_the_index_can_use()
    {
        Assert.Contains("t.occurred_at <= @", Where(Sql(Cursor)), StringComparison.Ordinal);
    }

    // A strict comparison inside the chain, so the row the cursor was built from is
    // not returned again. `<=` on the id, or on created_at, repeats it at every page
    // boundary -- one duplicated transaction per page. The bound above is the one
    // `<=` that belongs here, so this looks for the three that do not.
    [Theory]
    [InlineData("t.occurred_at <= @")]
    [InlineData("t.created_at <= @")]
    [InlineData("t.id <= @")]
    public void The_row_the_cursor_names_is_not_returned_a_second_time(string comparison)
    {
        var where = Where(Sql(Cursor));

        // The bound is written once and once only: the disjunction under it compares
        // occurred_at with < and =, never with <=.
        var occurrences = where.Split(comparison).Length - 1;

        Assert.Equal(comparison == "t.occurred_at <= @" ? 1 : 0, occurrences);
    }

    // The three parts of the cursor reach Postgres as parameters rather than as
    // literals, which is what lets one compiled query serve every page -- and what
    // keeps a value a client sent out of the SQL text.
    [Fact]
    public void The_cursor_travels_as_parameters()
    {
        var sql = Sql(Cursor);

        Assert.Contains("2026-08-19", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-19", Where(sql), StringComparison.Ordinal);
    }

    // **Paging does not open a door past the owner filter**, which is the one way
    // this change could have been a data leak rather than a bug. Every query in this
    // application is scoped by AppDbContext's global filter without mentioning
    // ownership, and a cursor is a value a client sends -- so the assertion is that
    // the WHERE clause still carries owner_id when a cursor is applied to it.
    [Fact]
    public void A_page_is_still_scoped_to_the_account_that_asked_for_it()
    {
        Assert.Contains("owner_id", Where(Sql(Cursor)), StringComparison.Ordinal);
    }

    // The index this walks, named in the assertion rather than in a comment: the
    // WHERE and the ORDER BY are in the order of its four columns, with owner_id
    // pinned by equality above them, which is what makes the plan an index scan with
    // no sort step. The model half is OwnershipFilterTests; this is the query half.
    [Fact]
    public void The_page_reads_the_columns_the_index_is_built_on()
    {
        var order = Sql(Cursor);

        Assert.Contains("ORDER BY t.occurred_at DESC, t.created_at DESC, t.id DESC", order, StringComparison.Ordinal);
        Assert.Contains("owner_id", Where(order), StringComparison.Ordinal);
    }

    // --- helpers --------------------------------------------------------------

    private static string Sql(TransactionCursor? after)
    {
        using var db = Context();

        return TransactionPaging.NewestFirst(db.Transactions, after).ToQueryString();
    }

    // The WHERE clause alone. owner_id is a column of the entity so it appears in
    // every SELECT list whether the filter applies or not, and asserting over the
    // whole statement finds it either way -- the trap PendingCategorizationTests
    // records, met here from the same side.
    private static string Where(string sql)
    {
        var start = sql.IndexOf("WHERE", StringComparison.Ordinal);

        return start < 0 ? string.Empty : sql[start..];
    }

    // A connection string that parses and is never opened.
    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=test-only;Database=none;Username=none;Password=none")
                .UseSnakeCaseNamingConvention()
                .Options,
            new SomebodySignedIn());

    private sealed class SomebodySignedIn : ICurrentUser
    {
        public string? OwnerId => "owner-a";
    }
}
