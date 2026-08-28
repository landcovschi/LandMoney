using LandMoney.Web.Import;

namespace LandMoney.Web.Tests.Import;

/// <summary>What counts as the same transaction twice.</summary>
public class TransactionKeyTests
{
    private static TransactionKey Key(decimal amount = 412.50m, string description = "linella") =>
        new(new DateOnly(2026, 6, 2), amount, "MDL", description);

    // **The one that would fail silently.** Postgres returns numeric(18,2) as 12.50
    // and a CSV may say 12.5; those are different bit patterns for the same number.
    // If the record struct's equality distinguished them, a re-import would insert
    // every row a second time and nothing anywhere would say so -- which is exactly
    // the failure the duplicate check exists to prevent. Asserted rather than
    // believed, on both halves of the contract, because a HashSet consults the hash
    // code before it consults Equals.
    [Fact]
    public void An_amount_of_12_5_and_one_of_12_50_are_the_same_key()
    {
        Assert.Equal(Key(12.5m), Key(12.50m));
        Assert.Equal(Key(12.5m).GetHashCode(), Key(12.50m).GetHashCode());
        Assert.Single(new HashSet<TransactionKey> { Key(12.5m), Key(12.50m) });
    }

    [Fact]
    public void A_different_amount_is_a_different_key()
    {
        Assert.NotEqual(Key(412.50m), Key(412.51m));
    }

    // Ordinal, deliberately: this is a duplicate check and not a search, and two
    // rows a person capitalised differently are two things they wrote differently.
    [Fact]
    public void A_description_differing_only_in_case_is_a_different_key()
    {
        Assert.NotEqual(Key(description: "linella"), Key(description: "Linella"));
    }

    [Fact]
    public void A_different_day_is_a_different_key()
    {
        Assert.NotEqual(
            new TransactionKey(new DateOnly(2026, 6, 2), 412.50m, "MDL", "linella"),
            new TransactionKey(new DateOnly(2026, 6, 3), 412.50m, "MDL", "linella"));
    }

    [Fact]
    public void A_different_currency_is_a_different_key()
    {
        Assert.NotEqual(
            new TransactionKey(new DateOnly(2026, 6, 2), 9.99m, "EUR", "netflix"),
            new TransactionKey(new DateOnly(2026, 6, 2), 9.99m, "MDL", "netflix"));
    }
}
