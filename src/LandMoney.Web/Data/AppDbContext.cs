using LandMoney.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Data;

public class AppDbContext : DbContext
{
    // This constructor is what AddDbContext needs: dependency injection builds the
    // options (provider, connection string) and hands them in. Without it the
    // failure arrives at first resolve, at run time, rather than at compile time.
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The two keys ListAsync sorts by, in the order it sorts by them. There
        // is exactly one query in this application and this is its shape, which
        // is the justification -- not the row count. At a few thousand personal
        // transactions Postgres will read the table and sort it, and be right to;
        // an index earns its keep when the table outgrows that, and the point of
        // declaring it now is that the shape of the query is known now.
        //
        // Not IsDescending(), although the query says DESC twice. A btree can be
        // walked backwards, so an ascending index serves `ORDER BY a DESC, b DESC`
        // with no sort step at all -- a descending index only buys something when
        // the directions are mixed. Writing DESC here would be copying the LINQ
        // and would cost an index Postgres cannot also use for the ascending case.
        //
        // The name comes from EFCore.NamingConventions like everything else:
        // ix_transactions_occurred_at_created_at.
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.OccurredAt, t.CreatedAt });
    }
}
