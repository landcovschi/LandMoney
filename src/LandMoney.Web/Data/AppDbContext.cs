using LandMoney.Web.Auth;
using LandMoney.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Data;

// IdentityDbContext rather than DbContext as of #52, which brings seven tables
// this application never queries: users, roles, the join between them, and four
// kinds of claim and token. AddEntityFrameworkStores<AppDbContext> is what reads
// them, and nothing else does.
//
// One context rather than two, which was the alternative and is what a larger
// system does -- a separate IdentityDbContext against its own schema keeps the
// domain tables free of the auth subsystem. It lost on the thing that decides it
// here: two contexts are two connection strings' worth of ceremony, two
// migration histories, two `dotnet ef` invocations with --context on every one,
// and a second efbundle in the deploy job. For a table count in single figures
// that is machinery bought against a separation nothing is asking for.
//
// IdentityUser, not a derived AppUser. There is no field to add: no display name
// (the username is the display name), no email (see AuthenticationSetup for why
// none is collected). A derived type with no members is a migration nobody
// needed.
public class AppDbContext : IdentityDbContext<IdentityUser>
{
    // The service, held; NOT the value, captured. That distinction is the whole of
    // a bug that reached a running application and was caught by sending requests
    // to it rather than by reading the code, and it is worth the paragraph.
    //
    // The first version of this stored `currentUser.OwnerId` in a string field
    // filled in the constructor, on the reasoning that a DbContext lives for one
    // request and is resolved when the endpoint's arguments are bound, which is
    // after authorization has run. That reasoning is wrong as soon as Identity is
    // involved: the cookie handler validates the security stamp during
    // UseAuthentication, which resolves SignInManager -> UserManager -> the
    // Entity Framework store -> this context. So the context is created while
    // HttpContext.User is still anonymous, the captured value is null, and it stays
    // null for the whole request because a scoped service is created once.
    //
    // What that looked like from outside is the reason it is written down here.
    // Every read answered `WHERE owner_id IS NULL`, every write stamped null, and
    // the screen therefore showed a consistent, plausible, shared list -- two
    // accounts seeing each other's spending, with no error anywhere. A filter that
    // fails to nothing is loud; this one failed to everything.
    //
    // Reading the property inside the expression is still parameterised, which was
    // the original concern. EF evaluates `_currentUser.OwnerId` client-side when
    // the query is executed -- it is not something it tries to translate -- and
    // puts the result in as a parameter, so one compiled query stays correct for
    // every user. The difference is only *when* it is read.
    private readonly ICurrentUser _currentUser;

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
        _currentUser = currentUser;
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // #52. UseSnakeCaseNamingConvention does not reach these seven, and finding
        // that out costs a schema that is half one thing and half the other.
        //
        // The convention renames what EF named by convention. IdentityDbContext
        // names its tables *explicitly* -- ToTable("AspNetUsers") and six more --
        // and an explicit name is a decision the convention is right not to
        // overrule. So the first run of this migration produced `transactions`
        // beside `AspNetUsers`, with the constraint and index names snake_cased
        // either way, because those were left to convention. Read out of the
        // running database with \dt rather than guessed at.
        //
        // That is exactly what #13 decided against: on Postgres a capital letter
        // in an identifier makes it a quoted identifier for ever, and the Python
        // service of slice 4 would meet column names nobody in that ecosystem
        // writes.
        //
        // Seven explicit lines rather than a loop over GetEntityTypes() with a
        // ToSnakeCase helper. The loop is shorter and needs a string function this
        // repository would then own a second copy of -- the package already has
        // one -- and it hides which tables it is renaming behind a StartsWith.
        // These are the only seven, they arrive with the package, and the list
        // changes only when Identity's schema does.
        modelBuilder.Entity<IdentityUser>().ToTable("asp_net_users");
        modelBuilder.Entity<IdentityRole>().ToTable("asp_net_roles");
        modelBuilder.Entity<IdentityUserClaim<string>>().ToTable("asp_net_user_claims");
        modelBuilder.Entity<IdentityUserRole<string>>().ToTable("asp_net_user_roles");
        modelBuilder.Entity<IdentityUserLogin<string>>().ToTable("asp_net_user_logins");
        modelBuilder.Entity<IdentityRoleClaim<string>>().ToTable("asp_net_role_claims");
        modelBuilder.Entity<IdentityUserToken<string>>().ToTable("asp_net_user_tokens");

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
        // Id joined it in #95, and it is a correctness column rather than a
        // performance one. The list is paged by a keyset cursor now, and a cursor
        // needs a *total* order to walk: OccurredAt ties within a day by design
        // (#17), and CreatedAt ties too -- measured at about fourteen rows per
        // identical value in a three-hundred row import, which is the arithmetic on
        // TransactionCursor. Without the primary key as a third key the order inside
        // those ties is whatever Postgres finds cheapest, and a page boundary landing
        // in one repeats or drops rows.
        //
        // It has to be *in the index* and not merely in the ORDER BY. With three sort
        // keys and two indexed columns Postgres can still walk the index and then
        // sort each tie group -- an incremental sort, cheap and bounded -- but that
        // is a sort step, and #95's acceptance test is an EXPLAIN with none. The
        // width it costs is 16 bytes an entry on one index.
        //
        // The name comes from EFCore.NamingConventions like everything else:
        // ix_transactions_owner_id_occurred_at_created_at_id.
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.OwnerId, t.OccurredAt, t.CreatedAt, t.Id });

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
        // Note what the null case does. When there is no signed-in user -- an
        // anonymous request, or a context built by `dotnet ef` -- this compares
        // owner_id to NULL,
        // which in SQL is never true, not even for a row whose owner_id is also
        // NULL. So an unauthenticated read returns nothing rather than everything,
        // and the pre-#52 rows are invisible to everyone until they are claimed.
        // Failing to a closed door is the point; it is also the one behaviour here
        // that is easy to change by accident, by "fixing" this into a filter that
        // is skipped when _ownerId is null.
        modelBuilder.Entity<Transaction>()
            .HasQueryFilter(t => t.OwnerId == _currentUser.OwnerId);
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
                entry.Entity.OwnerId = _currentUser.OwnerId;
            }
        }
    }
}
