using System.Linq.Expressions;
using LandMoney.Web.Api;
using LandMoney.Web.Models;

namespace LandMoney.Web.Categorizing;

/// <summary>Which rows the sweep is allowed to categorise. #92.</summary>
// A named expression rather than a lambda written into the query, which is the
// shape #89 arrived at for the export's one WHERE clause and for the same two
// reasons. A test can hold an Expression and assert what it selects without a
// database; and a rule with a name is a thing somebody has to delete on purpose,
// where a lambda inside a LINQ chain is a thing somebody edits while reading past
// it. This one guards more than that one did: getting it wrong either re-predicts
// over a person's own labelling or bills for a row for ever.
//
// Expression, never Func. A Func compiles to a delegate EF cannot translate, so
// the provider would fetch every transaction in the database -- every owner's --
// and filter them in memory. That failure is silent, correct, and unusable.
public static class PendingCategorization
{
    /// <summary>The rows the sweep may claim, given the cap on attempts.</summary>
    // Three conditions, and each one is load-bearing in a different direction.
    //
    // **`CategorizationAttempts != null`** is the marker itself: null means
    // nothing is owed. It is what keeps the sweep away from rows that predate #92
    // and, far more importantly, away from a row whose category a person
    // deliberately cleared -- #63 records that clearing writes null to both
    // category columns, so `category IS NULL` cannot tell that row from a fresh
    // one. See the column's own comment on Transaction.
    //
    // **`< maxAttempts`** is the ceiling #92's fourth trap asks for: a sweep that
    // retries for ever is an unbounded bill once the model is on. Reaching the cap
    // takes the row out of this predicate and leaves the count standing, so the
    // giving-up is recorded rather than erased.
    //
    // **`CategorySource != Human`** is CategorySources.MayOverwrite, and this is
    // the first caller where that rule is not trivially true. In CreateAsync the
    // transaction was constructed thirty lines above and had no source at all, so
    // the guard could not be false; here the sweep is looking at rows that have
    // been sitting in the database, and one of them may have been labelled by hand
    // between the save and the sweep. It is spelled out rather than called,
    // because MayOverwrite is a method and EF cannot translate a method call into
    // SQL -- so the two are pinned to each other by a test instead.
    //
    // The null case in that last condition is the one to check rather than assume:
    // in SQL, `category_source <> 'human'` is unknown when the column is null, so
    // a literal translation would exclude exactly the rows the sweep exists for.
    // EF Core's null semantics rewrite it to `(category_source <> 'human' OR
    // category_source IS NULL)`, which is right -- and PendingCategorizationTests
    // asserts it against the generated SQL rather than trusting the paragraph.
    public static Expression<Func<Transaction, bool>> Owed(int maxAttempts) =>
        transaction =>
            transaction.CategorizationAttempts != null
            && transaction.CategorizationAttempts < maxAttempts
            && transaction.CategorySource != CategorySources.Human;

    /// <summary>The rows a backfill may put into the queue, given the cap. #93.</summary>
    // The sweep only ever sees rows something already marked as owing a category, so
    // there has to be a way to mark rows nothing marked -- #62's imported ones, which
    // arrive with no category by design, and the rows the sweep gave up on while the
    // categorizer was down. This is that predicate, and it is deliberately not the
    // obvious `Category == null`.
    //
    // **`CategorySource != Human` is the trap #93 names**, and it is the same
    // condition <see cref="Owed"/> carries for the same reason: a row a person
    // labelled must never be predicted over. `CategorySources.MayOverwrite` is the
    // rule and cannot be called here, because EF cannot translate a method into SQL;
    // the two are pinned to each other by a test instead.
    //
    // **`Category == null` and not "has no useful category"**, which means a row a
    // person deliberately *cleared* is marked again and asked about again. That is
    // #63's known hole -- clearing writes null to both columns, so a cleared row is
    // indistinguishable from one nothing ever touched -- and #63 says to reopen it
    // "the day something re-categorises existing rows", which is today. It is
    // accepted rather than closed: the backfill is an explicit act by the same person
    // who did the clearing, it overwrites a blank rather than a label, and the
    // alternative #63 costed -- storing `category = null, source = human` -- breaks
    // the invariant that a source exists exactly when a category does, which three
    // files now rely on.
    //
    // **The attempts condition is what makes running it twice safe.** A row that is
    // already in the queue is left alone, so a second backfill does not reset the
    // budget of a row the sweep is halfway through. A row that reached the cap is
    // picked up again, which is the whole of "anything the categorizer missed while
    // it was down".
    //
    // What it does *not* protect against, said out loud: a row the categorizer
    // answered and abstained on looks exactly like a row nothing ever asked about --
    // both have no category, no source and no marker. So a second backfill asks about
    // those again. That is deliberate rather than tolerated: it is how a switch from
    // the rules to the model reaches the rows the rules declined, and it is why the
    // count is put on the screen before the button is pressed rather than reported
    // after.
    public static Expression<Func<Transaction, bool>> Backfillable(int maxAttempts) =>
        transaction =>
            transaction.Category == null
            && transaction.CategorySource != CategorySources.Human
            && (transaction.CategorizationAttempts == null
                || transaction.CategorizationAttempts >= maxAttempts);

    /// <summary>How many charged attempts a row gets before the sweep gives up on it.</summary>
    // The ceiling #92's fourth trap asks for, and it lives here rather than on
    // CategorizerSweep because it has two readers: the sweep, which applies it, and
    // the response projection, which uses it to tell a client whether a category is
    // still coming. Both must mean the same number or the screen promises something
    // the server has stopped doing.
    //
    // Thirty, against a five-second sweep, is two and a half minutes of continuous
    // failure before the oldest owed row is abandoned. The arithmetic is worth
    // doing rather than inheriting: the categorizer scales to zero (#61), so the
    // first call after an idle spell is a probable timeout -- charged, because
    // CountsAgainstTheCap cannot tell a stopped container from a slow model -- and
    // one wasted attempt per idle period must not be able to exhaust this.
    //
    // Configuration overrides it for the sweep. The projection deliberately reads
    // this default instead, which is written down on the projection.
    public const int DefaultMaxAttempts = 30;

    /// <summary>What a row is set to when it is created and a category is owed for it.</summary>
    // Zero rather than null, and it is the only place a row enters the queue.
    // Written as a named constant so that "a new transaction owes a
    // categorisation" is one fact with one spelling, and so the create path does
    // not read as though it were choosing a number.
    public const int Owing = 0;
}
