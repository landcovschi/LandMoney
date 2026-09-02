using LandMoney.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Api;

/// <summary>The order the list is in, how far a page reaches, and where it stops. #95.</summary>
// Pulled out of the handler rather than written inline, and unlike LabelledRows and
// PendingCategorization the reason is not only that a rule wants a name. **The
// ordering and the cursor's comparison are one decision written in two places, and
// they are wrong together or not at all.** A third sort key added here without the
// matching condition in TransactionCursor.After repeats rows; the condition without
// the key skips them. Keeping them in one file with one test that reads the SQL both
// produce is what makes that a change somebody makes on purpose.
//
// It is also the only way any of this can be tested at all. The handler reaches
// AppDbContext, which is the wall AuthorizationTests and #62 both document -- but an
// IQueryable is a value, and ToQueryString turns it into the command EF would send
// without opening a connection.
public static class TransactionPaging
{
    /// <summary>How many rows a client gets when it does not say. </summary>
    // Fifty, which is more than a month of one person's spending and about two
    // screens. The number is not load-bearing -- the whole point of a cursor is that
    // the next page costs the same as the first -- so it is chosen for the first
    // request rather than for the total: fifty rows is roughly 12 KB of JSON, and the
    // page that pays the container's 23-second cold start (#35) is this one.
    public const int DefaultPageSize = 50;

    /// <summary>The most a client may ask for in one request.</summary>
    // A ceiling rather than a target, and it is the reason this endpoint is now
    // bounded at all -- which is the whole of #95. Without it `?limit=1000000` is
    // GET /api/transactions before this change, with a query parameter in front of
    // it.
    //
    // Two hundred rather than something tighter, because the client asks for a
    // window rather than a page in one place: App refreshes the rows it is already
    // showing in a single request while a category is still on its way, and that
    // window is as large as the reader has scrolled. A list grown past this by
    // pressing "Load more" five times stops being refreshed past row 200 until the
    // next reload, which is written down on the poll rather than guarded against.
    public const int MaxPageSize = 200;

    /// <summary>What a client actually gets, given what it asked for.</summary>
    // Clamped rather than refused, and the asymmetry with the cursor beside it is
    // deliberate. A cursor that does not parse names a place that does not exist and
    // there is nothing to do but say so; a limit of 5,000 names a real intention this
    // server declines to honour in full, and answering 200 rows is a complete answer
    // to it. Zero and negative numbers are the same case -- an unusable request with
    // an obvious reading -- and become the default rather than an empty page, since
    // an empty page from a non-empty table is the one answer that looks like a bug.
    public static int ClampPageSize(int? requested) => requested switch
    {
        null => DefaultPageSize,
        <= 0 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => requested.Value,
    };

    /// <summary>The list's order, starting after <paramref name="after"/> when there is one.</summary>
    // **Three sort keys, and the third exists because the second is not unique.**
    // OccurredAt became a DateOnly in #17, so an ordinary week has several rows
    // sharing a day; CreatedAt was the tiebreak for that and is itself shared by
    // about fourteen rows in every three hundred an import writes, measured on the
    // comment above TransactionCursor. Without a third key the order within those
    // ties is whatever Postgres finds cheapest -- which looks stable in testing,
    // changes when the table is next rewritten, and takes the cursor with it.
    //
    // The id is a random v4 Guid, so it orders rows within a tie meaninglessly. That
    // is the point: what a total order buys here is not a *better* order but a
    // *repeatable* one, which is the only property a cursor needs from it.
    //
    // DESC on all three, and the index is ascending on all three -- deliberately, and
    // the reasoning is #37's, unchanged: a btree is walked backwards just as cheaply,
    // and a descending index only pays when the directions are mixed. What makes it
    // work is that all three agree, so `Index Scan Backward` reads the page with no
    // sort step at all. Mixing one of them would cost exactly the sort this exists
    // to avoid.
    public static IQueryable<Transaction> NewestFirst(
        IQueryable<Transaction> transactions,
        TransactionCursor? after)
    {
        // The filter first and the order second, which reads backwards and is what
        // EF wants: Where after OrderBy is legal and produces the same SQL, and
        // writing it this way round keeps the shape of the statement -- WHERE then
        // ORDER BY -- the same as the shape of the code.
        var rows = after is null ? transactions : transactions.Where(TransactionCursor.After(after));

        return rows
            .OrderByDescending(transaction => transaction.OccurredAt)
            .ThenByDescending(transaction => transaction.CreatedAt)
            .ThenByDescending(transaction => transaction.Id);
    }

    /// <summary>Reads one page, plus the one row that says whether there is another.</summary>
    // **Take(size + 1) rather than a second COUNT, and it is what makes the last page
    // knowable.** A cursor built from the final row of every page is always non-null,
    // so a client following it discovers the end by asking for a page that comes back
    // empty -- one wasted round trip per list, every time, and a "Load more" button
    // that stays on the screen after there is nothing more to load. One extra row
    // answers the same question inside the page that raised it.
    //
    // The extra row is fetched and thrown away, which is the honest cost: one row of
    // work and one row of bytes off the index scan that was happening anyway.
    public static async Task<(List<T> Rows, bool HasMore)> PageAsync<T>(
        IQueryable<T> query,
        int pageSize,
        CancellationToken cancellationToken) =>
        TrimToPage(await query.Take(pageSize + 1).ToListAsync(cancellationToken), pageSize);

    /// <summary>Drops the lookahead row, and says whether there was one.</summary>
    // Split out of the query for one reason and it is not tidiness: ToListAsync
    // needs EF's own query provider, so a List handed to PageAsync throws rather
    // than being paged, and this arithmetic could otherwise only be checked against
    // a database. It is three lines and the off-by-one in it is the difference
    // between a "Load more" button that never goes away and a table missing its
    // last row.
    public static (List<T> Rows, bool HasMore) TrimToPage<T>(List<T> rows, int pageSize)
    {
        if (rows.Count <= pageSize)
        {
            return (rows, false);
        }

        // RemoveAt rather than Take(pageSize) on the materialised list, so the
        // returned list is the one that was allocated rather than a copy of it. The
        // reason to say so is that it is the last row that goes, and removing the
        // first would silently drop the newest transaction on every page but the
        // last -- a mistake that reads identically and hides behind a screen that
        // still fills up.
        rows.RemoveAt(rows.Count - 1);

        return (rows, true);
    }
}
