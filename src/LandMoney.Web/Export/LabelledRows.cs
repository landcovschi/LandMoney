using System.Linq.Expressions;
using LandMoney.Web.Api;
using LandMoney.Web.Models;

namespace LandMoney.Web.Export;

/// <summary>Which rows may leave in an export, as a rule rather than as a lambda.</summary>
// #89's first trap, and the one sentence the whole issue exists for: "a `model` row
// exported into the eval set is the predictor grading its own past answers, and the
// number afterwards means nothing". It is one WHERE clause, and a WHERE clause
// written inline in a handler is a thing nothing can hold: the handler reaches
// AppDbContext, so no test in this suite can call it, and the clause would be
// checkable only by reading it.
//
// Named and pulled out for the same reason CategorySources.MayOverwrite is, which is
// the closest precedent in this repository: a rule that is easy to lose, in a place
// where losing it is silent. #63 declined to extract the ternary next to it and was
// right to -- that is indirection around a conditional with its explanation directly
// above it. This is the difference: two conditions, an invariant between them, and a
// failure that produces a plausible file and a meaningless number.
//
// An Expression rather than a Func, because EF has to translate it into SQL. A Func
// would compile, and would silently pull every transaction into memory to filter it
// there -- the shape of mistake that is invisible until the table is large.
//
// **What this still cannot hold**, said plainly: that the handler applies it. A test
// can assert the rule and cannot assert the call site, because the call site needs a
// database. Deleting the `.Where` is a one-line change nothing here reports, and it
// was checked by hand instead -- against the compose stack, with a `rules` row that
// had to stay out of the file and did.
public static class LabelledRows
{
    /// <summary>A row a person decided, and therefore a row worth scoring against.</summary>
    public static readonly Expression<Func<Transaction, bool>> ByHand =
        transaction =>
            // CategorySources.Human is written by the PATCH handler and by nothing
            // else, and no service can send it -- AuthenticationSetup's note on the
            // three producers says so. That is what makes this a statement about who
            // decided rather than about what happens to be stored.
            transaction.CategorySource == CategorySources.Human

            // Redundant while #59's invariant holds -- a source exists exactly when a
            // category does, which is why clearing a category clears both columns --
            // and kept because of what it costs if it ever stops. An empty fifth
            // field is not a row evals/score.py skips; it refuses the *whole file*,
            // naming a label outside the vocabulary. Dropping such a row is the
            // failure that leaves the export usable.
            && transaction.Category != null;
}
