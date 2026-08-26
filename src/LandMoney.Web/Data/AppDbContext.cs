using LandMoney.Web.Auth;
using LandMoney.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Data;

public class AppDbContext : DbContext
{
    // Captured at construction rather than read from ICurrentUser inside the
    // filter, and both halves of that matter.
    //
    // EF Core turns a query filter into SQL once and caches it. A filter that
    // closes over a field of the context instance is compiled with that field as a
    // query *parameter*, which is what makes one cached plan correct for every
    // user; a filter that called a method on a service would be a method EF has to
    // translate, and it cannot. This is the documented multi-tenancy shape.
    //
    // The value is therefore fixed for the lifetime of this context, which is one
    // request -- AddDbContext registers it scoped, and the context is resolved
    // when the endpoint's arguments are bound, after authorization has run. There
    // is no point in a request at which this would change.
    private readonly string? _ownerId;

    // This constructor is what AddDbContext needs: dependency injection builds the
    // options (provider, connection string) and hands them in. Without it the
    // failure arrives at first resolve, at run time, rather than at compile time.
    //
    // ICurrentUser was added in #52 and it is resolved here in places no HTTP
    // request reaches: `dotnet ef` builds this context to read the model, and so
    // does the migration bundle. Both get a CurrentUser with no HttpContext, which
    // answers null -- correct, and harmless, because neither of them queries.
    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        _ownerId = currentUser.OwnerId;
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The keys ListAsync filters and sorts by, in the order it uses them. There
        // is exactly one query in this application and this is its shape, which
        // is the justification -- not the row count. At a few thousand personal
        // transactions Postgres will read the table and sort it, and be right to;
        // an index earns its keep when the table outgrows that, and the point of
        // declaring it now is that the shape of the query is known now.
        //
        // OwnerId leads it as of #52, and the reason is that the shape changed
        // rather than that a column was added. The query is now
        // `WHERE owner_id = @p ORDER BY occurred_at DESC, created_at DESC`, and an
        // index that does not start with the equality column cannot serve the
        // filter -- so ix_transactions_occurred_at_created_at, correct for #37's
        // query, answers nothing about this one. With owner_id pinned to a single
        // value by equality, the two remaining columns are still in sort order
        // within it, which is what keeps the sort step away.
        //
        // The old index is replaced rather than kept beside this one. Any query
        // this application makes is filtered by owner -- the global filter below
        // guarantees it -- so there is no longer a query the two-column index is
        // the better answer for, and a second index is paid for on every write.
        //
        // Not IsDescending(), although the query says DESC twice. A btree can be
        // walked backwards, so an ascending index serves `ORDER BY a DESC, b DESC`
        // with no sort step at all -- a descending index only buys something when
        // the directions are mixed. Writing DESC here would be copying the LINQ
        // and would cost an index Postgres cannot also use for the ascending case.
        //
        // The name comes from EFCore.NamingConventions like everything else:
        // ix_transactions_owner_id_occurred_at_created_at.
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.OwnerId, t.OccurredAt, t.CreatedAt });

        // #52, and this line is the whole of the issue's sharpest warning: "every
        // query gains a filter, and the one that forgets it is a data leak rather
        // than a bug".
        //
        // A global query filter is chosen precisely so that there is nothing to
        // forget. The alternative -- a .Where(t => t.OwnerId == ...) written into
        // each query by hand -- is correct today, when there is one query, and it
        // is one new endpoint away from being wrong for ever, with no compiler and
        // no test that would notice. Here the filter is the default and the
        // exception has to be asked for by name (IgnoreQueryFilters), which is a
        // call that shows up in a diff and can be searched for. There is currently
        // no such call anywhere in this repository.
        //
        // What this does NOT cover, and it is the reason SaveChanges is overridden
        // below: a filter applies to reads. Nothing about it stops a row being
        // inserted with somebody else's owner, or with none.
        //
        // Note what the null case does. When _ownerId is null -- nobody signed in,
        // or a context built by `dotnet ef` -- this compares owner_id to NULL,
        // which in SQL is never true, not even for a row whose owner_id is also
        // NULL. So an unauthenticated read returns nothing rather than everything,
        // and the pre-#52 rows are invisible to everyone until they are claimed.
        // Failing to a closed door is the point; it is also the one behaviour here
        // that is easy to change by accident, by "fixing" this into a filter that
        // is skipped when _ownerId is null.
        modelBuilder.Entity<Transaction>()
            .HasQueryFilter(t => t.OwnerId == _ownerId);
    }

    // The write half of the invariant the query filter enforces on reads. Both
    // exist for the same reason: ownership is not something a call site should be
    // able to get wrong, and TransactionEndpoints.CreateAsync deliberately does
    // not mention OwnerId at all.
    //
    // Unconditional, not "set it if it is empty". A value already on the entity
    // could only have come from a caller inventing one, and there is no legitimate
    // caller that knows better than the signed-in subject. Overwriting is the
    // behaviour that makes that impossible rather than merely unlikely.
    //
    // Modified entities are left alone: nothing in this application transfers a
    // row between owners, and stamping on update would silently move a row into
    // the reader's account. Since the query filter means a context can only load
    // rows it already owns, an update cannot reach somebody else's row to begin
    // with.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampOwner();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampOwner();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Both overloads of each method funnel through the ones above -- the
    // parameterless SaveChanges() calls SaveChanges(true) -- so overriding the
    // two-argument forms covers all four. Overriding all four instead would stamp
    // twice on every save.
    private void StampOwner()
    {
        foreach (var entry in ChangeTracker.Entries<Transaction>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.OwnerId = _ownerId;
            }
        }
    }
}
