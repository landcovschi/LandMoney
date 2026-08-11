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
}
