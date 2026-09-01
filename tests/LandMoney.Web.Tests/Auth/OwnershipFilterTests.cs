using LandMoney.Web.Auth;
using LandMoney.Web.Data;
using LandMoney.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Tests.Auth;

/// <summary>That the global query filter reaches the SQL, and what it says there.</summary>
// ToQueryString builds the command EF would send without opening a connection, so
// these run with no Postgres, no Docker and no network -- the property CLAUDE.md
// records for this suite and the reason #22's job is `dotnet test` and not
// `dotnet test` plus a service container.
//
// What that buys and what it does not. It proves the filter is applied, that it is
// parameterised rather than baked in, and that a signed-out context does not
// quietly ask for everything. It cannot prove what Postgres then returns -- that
// `owner_id = NULL` matches no row, including a row whose owner_id is also null,
// is a fact about SQL three-valued logic rather than about this code. That half is
// verified against the running database and written up in docs/deploy-azure.md.
//
// This is also the first test in the repository to touch EF Core, which is what
// the version pin in the .csproj was put there for in #21. The comment beside it
// predicted a FileLoadException "the first time a test touches EF"; the pin held.
public class OwnershipFilterTests
{
    [Fact]
    public void The_list_query_is_filtered_by_owner()
    {
        using var db = ContextFor("owner-a");

        var sql = db.Transactions.ToQueryString();

        Assert.Contains("owner_id", sql, StringComparison.Ordinal);
    }

    // The value must arrive as a parameter, not as a literal. Two reasons, and the
    // second is the one that would be discovered slowly: a literal would put one
    // user's subject into the query text, so EF's plan cache would hold a separate
    // entry per user, and Postgres would too. This is also what makes one compiled
    // filter correct for every user, which is why AppDbContext captures the owner
    // in a field rather than calling a service from inside the expression.
    //
    // Note what is being read. ToQueryString prints the parameter *declarations*
    // above the statement -- "-- @__ef_filter___ownerId='owner-a'" -- so the value
    // does appear in its output, and a naive DoesNotContain over the whole string
    // fails against correct code. The statement itself is what has to be free of
    // it, which is everything from the first SELECT.
    [Fact]
    public void The_owner_is_a_parameter_rather_than_a_literal()
    {
        using var db = ContextFor("owner-a");

        var sql = db.Transactions.ToQueryString();
        var statement = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];

        Assert.DoesNotContain("owner-a", statement, StringComparison.Ordinal);
        Assert.Contains("@", statement, StringComparison.Ordinal);
    }

    // The mutation this is here to kill: "the filter should not apply when nobody
    // is signed in", which reads like a sensible guard for `dotnet ef` and is the
    // difference between a signed-out read returning nothing and returning
    // everything. Nothing in the application reaches this state -- every endpoint
    // requires authorization -- which is precisely why it has to be asserted
    // rather than reasoned about.
    [Fact]
    public void A_context_with_nobody_signed_in_still_filters()
    {
        using var db = ContextFor(null);

        var sql = db.Transactions.ToQueryString();

        Assert.Contains("owner_id", sql, StringComparison.Ordinal);
    }

    // IgnoreQueryFilters is the one call that turns this off, and it is the call to
    // notice in a review. Asserted so that the suite says out loud that the escape
    // hatch exists and what it does.
    //
    // **This used to end "nothing in src/ calls it", and since #92 something does.**
    // CategorizerSweep is the one caller and is expected to be the only one: it
    // runs outside a request, so there is no signed-in user for the filter to
    // compare against, and without this call it would select nothing for ever while
    // looking exactly like a categorizer that was never reached. A second caller
    // appearing in src/ is a thing to argue about in review rather than a thing to
    // wave through -- which is the whole reason the escape hatch has to be asked for
    // by name.
    [Fact]
    public void IgnoreQueryFilters_is_the_only_way_past_it()
    {
        using var db = ContextFor("owner-a");

        var sql = db.Transactions.IgnoreQueryFilters().ToQueryString();

        Assert.DoesNotContain("WHERE", sql, StringComparison.Ordinal);
    }

    // The index #52 replaced, asserted through the model rather than through a
    // plan. The query is now `WHERE owner_id = @p ORDER BY occurred_at DESC,
    // created_at DESC`, and an index not starting with the equality column cannot
    // serve the filter -- so this is the shape, in the order that matters.
    [Fact]
    public void The_index_leads_with_the_column_the_query_filters_on()
    {
        using var db = ContextFor("owner-a");

        var index = Assert.Single(db.Model.FindEntityType(typeof(Transaction))!.GetIndexes());

        Assert.Equal(
            [
                nameof(Transaction.OwnerId),
                nameof(Transaction.OccurredAt),
                nameof(Transaction.CreatedAt),
            ],
            index.Properties.Select(p => p.Name));
    }

    // The regression test for the bug that reached a running application, and the
    // only one in this file that could not have been written by reading the code.
    //
    // The first version of AppDbContext captured currentUser.OwnerId in the
    // constructor. That is wrong with Identity in the pipeline: the cookie handler
    // validates the security stamp during UseAuthentication, which resolves
    // UserManager, which resolves the store, which resolves this context -- so it
    // is built while HttpContext.User is still anonymous, and the captured null
    // stays null for the rest of the request.
    //
    // The failure was invisible from inside: reads answered `WHERE owner_id IS
    // NULL` and writes stamped null, so two accounts saw one consistent shared
    // list with no error anywhere. Two users, one view, and every unit test in this
    // file still green -- because they all construct the context with the owner
    // already known, which is the one thing production does not do.
    //
    // So this one changes the owner AFTER construction, which is exactly what a
    // request does.
    [Fact]
    public void The_owner_is_read_when_the_query_runs_and_not_when_the_context_is_built()
    {
        var user = new StubCurrentUser(null);
        using var db = ContextForUser(user);

        // Authentication happens here, in effect: the principal arrives after the
        // context exists.
        user.OwnerId = "owner-a";

        var sql = db.Transactions.ToQueryString();

        // Under the bug this reads `WHERE t.owner_id IS NULL`, and every row
        // belonging to nobody is returned to everybody.
        Assert.DoesNotContain("IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("owner-a", sql, StringComparison.Ordinal);
    }

    private static AppDbContext ContextFor(string? ownerId) =>
        ContextForUser(new StubCurrentUser(ownerId));

    // Two methods rather than one overload taking ICurrentUser. `ContextFor(null)`
    // is ambiguous between `string?` and an interface, and the compiler says so --
    // CS0121, which reads as a mistake in the test rather than as a name that has
    // to be different.
    private static AppDbContext ContextForUser(ICurrentUser currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()

            // A connection string that parses and is never opened. ToQueryString
            // needs the provider to translate the expression tree; it does not need
            // a server, and there is none.
            .UseNpgsql("Host=test-only;Database=none;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, currentUser);
    }

    // Not a mock and not derived from anything -- ICurrentUser has one property,
    // which is the whole argument for it being an interface rather than a reach
    // into IHttpContextAccessor from inside AppDbContext.
    //
    // Settable rather than readonly, which is not laziness: the test above needs to
    // change the answer after the context has been built, because that is what
    // signing in does to a request that is already in flight.
    private sealed class StubCurrentUser(string? ownerId) : ICurrentUser
    {
        public string? OwnerId { get; set; } = ownerId;
    }
}
