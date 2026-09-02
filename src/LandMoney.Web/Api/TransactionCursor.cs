using System.Buffers.Text;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using LandMoney.Web.Models;

namespace LandMoney.Web.Api;

/// <summary>Where a page of the list stopped, as an opaque token. #95.</summary>
// Keyset paging rather than OFFSET, which #95 asks for by name and which the index
// has been shaped for since #37: ix_transactions_owner_id_occurred_at_created_at is
// exactly the thing a cursor walks. OFFSET is four fewer lines and re-reads every
// row it skips, so page fifty costs fifty pages of work -- and it shifts under an
// insert, so a back-dated entry added mid-scroll pushes one row across the boundary
// and it is read twice. A cursor names a *place in the order*, so an insert before
// it is simply a row the reader has already passed.
//
// **Three keys, not the two #95 names, and the third is not defensive
// programming.** The trap the issue records -- "the cursor has to carry both sort
// keys" -- has a second floor underneath it: two rows can share created_at as well.
// Measured rather than reasoned about, by constructing three hundred transactions
// the way ImportAsync does and truncating CreatedAt to the microsecond Postgres
// stores:
//
//     raw distinct 153 of 300; microsecond distinct 21 of 300
//
// So a three-hundred row import produces about fourteen rows per identical
// created_at, and a cursor on two keys would drop or repeat every one of them. The
// primary key is the tiebreak because it is the only column here that cannot tie.
//
// **What that costs is that .NET and Postgres do not agree how to order a uuid.**
// Guid.CompareTo compares the first three fields as integers; Postgres compares
// uuid as sixteen bytes. Neither is wrong and nothing here is broken, because both
// the comparison and the ORDER BY happen in Postgres -- but it means the order
// cannot be reproduced in C#, so nothing may sort a page client-side and expect to
// agree with the server. TransactionPaging.NewestFirst is where that pair is kept
// together.
public sealed record TransactionCursor(DateOnly OccurredAt, DateTimeOffset CreatedAt, Guid Id)
{
    // Not a character that can appear inside any of the three parts: a day and an
    // instant are digits, dashes, colons and dots, and a Guid in "D" is hex and
    // dashes. So a bar splits into exactly three fields or the token is not one.
    private const char Separator = '|';

    /// <summary>The day, formatted the one way this repository formats a day.</summary>
    // InvariantCulture, spelled out, and it is the rule #31 exists for rather than a
    // style. An interpolated {date:yyyy-MM-dd} takes the *current* culture's
    // calendar, so under ar-SA it writes a Hijri year -- the same format string, a
    // different calendar, silently. A cursor is a value that crosses a boundary and
    // comes back, so a machine formatting one calendar and parsing another would
    // answer 400 to a token it wrote itself.
    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>The instant, round-tripped to the tick.</summary>
    // "O" keeps seven fractional digits and the offset. Postgres stores six, so a
    // value read back out of the database has a zero in the seventh and survives the
    // round trip exactly -- which is the only case there is, because a cursor is
    // always built from a row that has just been read.
    //
    // **The InvariantCulture passed beside it is inert, and a mutation proved it
    // rather than a reading.** Removing it kills nothing: the round-trip specifier
    // is culture-independent by definition, so unlike DayFormat above it cannot
    // produce a Hijri year. It is written anyway, and it is the one argument in this
    // file kept for symmetry rather than for behaviour -- the day this format string
    // is changed to anything else, the culture is already named.
    private const string InstantFormat = "O";

    /// <summary>How long a token may be before it is refused unread.</summary>
    // The three parts are 10, 33 and 36 characters plus two separators: 81, which
    // base64 rounds up to 108. Twice that leaves room for a change of format and is
    // far less than a URL will carry.
    //
    // **It is a cost guard and not a behaviour, and a mutation is what settled
    // that.** Deleting it kills nothing, because a megabyte of rubbish decodes to
    // rubbish, splits into one field and is refused by the shape check below either
    // way. What it stops is the decode buffer being allocated at the size of
    // whatever was sent. Said out loud because the line reads like a validation rule
    // and is not one, and because the alternative to writing this down is somebody
    // later deleting it on the evidence that no test cares.
    private const int MaxEncodedLength = 256;

    /// <summary>The token that asks for the rows after this one.</summary>
    public static string Encode(TransactionResponse row) =>
        new TransactionCursor(row.OccurredAt, row.CreatedAt, row.Id).Encode();

    /// <summary>The token for this position.</summary>
    // Base64url rather than the three fields as three query parameters, and only the
    // second of the two reasons is about a tidy URL. A client that can read the
    // parts is a client that will eventually build one, and then the *shape* of the
    // cursor becomes a contract rather than an implementation detail -- adding the
    // id to it would have been a breaking change instead of a fix. And "+" in a
    // query string means a space unless it is escaped, which the offset in an
    // instant has.
    public string Encode()
    {
        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{OccurredAt.ToString(DayFormat, CultureInfo.InvariantCulture)}{Separator}{CreatedAt.ToString(InstantFormat, CultureInfo.InvariantCulture)}{Separator}{Id:D}");

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Reads a token back, or answers false. Never throws.</summary>
    // False rather than an exception, because every caller is a request parameter: a
    // token that does not parse is a 400 with a sentence in it, and the endpoint is
    // the place that knows how to say so. There is nothing here worth
    // distinguishing -- truncated, edited, from an older format, or typed by hand
    // are all "this is not a cursor I wrote".
    //
    // It deliberately does not check that the row still exists. A cursor names a
    // *position in the order* and not a row, so it keeps working after the row it
    // was built from is deleted -- which is exactly what should happen to somebody
    // paging through a list while a row above them goes away.
    public static bool TryParse(string? text, out TransactionCursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrEmpty(text) || text.Length > MaxEncodedLength)
        {
            return false;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(text.Length)];

        if (Base64Url.DecodeFromChars(text, buffer, out _, out var decoded)
            != System.Buffers.OperationStatus.Done)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(buffer, 0, decoded).Split(Separator);

        if (parts.Length != 3)
        {
            return false;
        }

        // ParseExact against one format, not TryParse against the current culture.
        // The same #31 rule as the writing half, and it is this half that bites: a
        // permissive parse would accept a token this application never wrote and
        // then start walking from a place nobody asked for.
        if (!DateOnly.TryParseExact(
                parts[0], DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return false;
        }

        // RoundtripKind, so the offset in the token is the offset that comes back
        // rather than being converted into this machine's local time.
        //
        // **Inert here, and a mutation is what says so**: DateTimeOffset.ParseExact
        // keeps the offset it parsed whatever the styles are, so swapping this for
        // None kills nothing. The flag earns its place the day this parses a
        // DateTime instead, where it is the difference between an instant and a
        // local reading of one -- and it is kept for that reason rather than
        // deleted, because the type is the only thing making it unnecessary.
        if (!DateTimeOffset.TryParseExact(
                parts[1],
                InstantFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAt))
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[2], "D", out var id))
        {
            return false;
        }

        cursor = new TransactionCursor(day, createdAt, id);

        return true;
    }

    /// <summary>The rows that come after this position, in the list's own order.</summary>
    // A named expression rather than a lambda written into the query, which is the
    // shape LabelledRows and PendingCategorization arrived at and for the same two
    // reasons: a test can hold an Expression and assert the SQL it becomes without a
    // database, and a rule with a name is a thing somebody has to delete on purpose
    // where a lambda inside a LINQ chain is a thing somebody edits while reading
    // past it. This one guards more than either of those -- getting it wrong shows a
    // reader the same transaction twice, or hides one from them for ever, and
    // neither is visible in a table small enough to check by eye.
    //
    // **The nesting is the whole of it, and the flat version is the classic bug.**
    // Written as `occurred_at <= @d AND created_at <= @c AND id < @i` it reads the
    // same and is wrong: it demands that *every* key be at or before the cursor, so
    // a row on an earlier day that happens to have a later created_at -- which is
    // most of them, the two being unrelated -- is skipped and never seen again.
    //
    // `Id.CompareTo(...) < 0` rather than `<`, because Guid carries no comparison
    // operator in C#. Npgsql translates it to a plain `t.id < @id`, checked with
    // ToQueryString rather than hoped for, and TransactionPagingTests goes on
    // checking it.
    //
    // Expression, never Func. A Func compiles to a delegate EF cannot translate, so
    // the provider would fetch every row and filter in memory -- which for a paging
    // predicate is precisely the failure paging exists to prevent, arriving silently
    // through the fix for it.
    public static Expression<Func<Transaction, bool>> After(TransactionCursor cursor) =>
        transaction =>
            // **Redundant, and it is the difference between keyset paging and OFFSET
            // in a cursor's clothes.** Everything the disjunction below can match
            // already satisfies this, so it changes no row -- what it changes is the
            // plan, because Postgres will push `occurred_at <= @d` into the index
            // and will not push the nested OR. Measured on 5,000 rows, the same page
            // in the middle of the list:
            //
            //     without:  Index Scan Backward ... Rows Removed by Filter: 1925
            //     with:     Index Cond: (owner_id = @o AND occurred_at <= @d)
            //               Rows Removed by Filter: 25
            //
            // Without it the scan starts at the newest row and discards everything
            // above the cursor -- which is exactly the work a cursor exists not to
            // do, arriving silently through the thing that was supposed to prevent
            // it. The 25 that remain are the one tie group at the boundary, which is
            // the irreducible part.
            //
            // The honest alternative is the row-value form Postgres pushes in whole,
            // `(occurred_at, created_at, id) < (@d, @c, @i)`. It is one clause and
            // it is what a hand-written query would say; EF Core translates no tuple
            // comparison, so taking it means FromSql and writing the SELECT list,
            // the projection and the owner filter out by hand -- the last of which
            // is the thing AppDbContext's global filter exists to make impossible to
            // forget. One redundant conjunct is the cheaper half of that trade.
            transaction.OccurredAt <= cursor.OccurredAt
            && (transaction.OccurredAt < cursor.OccurredAt
                || (transaction.OccurredAt == cursor.OccurredAt
                    && (transaction.CreatedAt < cursor.CreatedAt
                        || (transaction.CreatedAt == cursor.CreatedAt
                            && transaction.Id.CompareTo(cursor.Id) < 0))));
}
