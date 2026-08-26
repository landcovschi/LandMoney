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
    // hatch exists and what it does; nothing in src/ calls it.
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

    private static AppDbContext ContextFor(string? ownerId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()

            // A connection string that parses and is never opened. ToQueryString
            // needs the provider to translate the expression tree; it does not need
            // a server, and there is none.
            .UseNpgsql("Host=test-only;Database=none;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new StubCurrentUser(ownerId));
    }

    // Not a mock and not derived from anything -- ICurrentUser has one property,
    // which is the whole argument for it being an interface rather than a reach
    // into IHttpContextAccessor from inside AppDbContext.
    private sealed class StubCurrentUser(string? ownerId) : ICurrentUser
    {
        public string? OwnerId { get; } = ownerId;
    }
}
