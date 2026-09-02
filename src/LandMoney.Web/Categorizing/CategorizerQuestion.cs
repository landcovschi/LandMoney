using System.Linq.Expressions;
using LandMoney.Web.Models;

namespace LandMoney.Web.Categorizing;

/// <summary>The three fields a categorizer is shown about a transaction. #94.</summary>
// Not a new fact -- `CategorizeRequest` in CategorizerContracts.cs has carried
// exactly these three since #39, and `CategorySuggestionRequest` copies them for
// #67's preview. What is new is that two places now need to reason about them as
// a *set* rather than pass them along, and both are about staleness:
//
//   * an edit has to decide whether it changed the question, because a category
//     predicted from a description nobody can see any more is worse than none;
//   * the sweep has to decide whether the row it is about to write to is still
//     the row it asked about, because the call it is answering started seconds
//     ago and an edit may have landed in between.
//
// Writing that set down once is what stops the two drifting. The failure if they
// do is silent in the way this repository keeps meeting: an edit that re-queues
// on a field the sweep does not guard would let a stale answer win, and a sweep
// that guards a field the edit ignores would leave a row owing a category for
// ever.
//
// **The date is deliberately absent, and that is a decision rather than an
// oversight.** CategorySuggestionRequest carries no date for the reason written
// there -- the day money was spent tells a predictor nothing -- so correcting a
// mistyped year is the one edit that costs no model call. The day a predictor is
// shown the date, this record is the one place that has to change and both
// callers follow.
public sealed record CategorizerQuestion(string Description, decimal Amount, string Currency)
{
    /// <summary>What the categorizer would be asked about this row as it stands.</summary>
    public static CategorizerQuestion About(Transaction transaction) =>
        new(transaction.Description, transaction.Amount, transaction.Currency);

    /// <summary>The rows that still hold the three values this question was built from.</summary>
    // An Expression rather than a Func, for the reason PendingCategorization spells
    // out at greater length: a Func compiles to a delegate EF cannot translate, so
    // the provider would fetch the table and filter it in memory -- silently,
    // correctly, and unusably.
    //
    // The three values are pulled into locals first so that EF sees captured
    // variables and parameterises them. Reading them off `this` inside the tree
    // works too, and produces a closure over a record the translator then has an
    // opinion about; locals are one fewer thing to be surprised by.
    //
    // **Comparing decimals here is not the same as comparing them in C#.** Postgres
    // stores this column as numeric(18,2), so a row saved as 78.5 comes back as
    // 78.50 -- different bit patterns, equal values. `decimal.Equals` compares
    // values and so does `=` in SQL, so both halves agree; TransactionKey already
    // depends on the same property for the import's duplicate detection, and #62
    // records what the alternative would have cost there (a silent double import).
    public Expression<Func<Transaction, bool>> Unchanged()
    {
        var description = Description;
        var amount = Amount;
        var currency = Currency;

        return transaction =>
            transaction.Description == description
            && transaction.Amount == amount
            && transaction.Currency == currency;
    }
}
