using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Models;

/// <summary>
/// One item of spending, entered by hand.
/// </summary>
public class Transaction
{
    public Guid Id { get; set; }

    /// <summary>When the money was actually spent. Typed by a human.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    // decimal, never double: binary floating point cannot represent 0.10 exactly.
    // Without an explicit precision EF Core takes the provider default and you find
    // out in production; this pins the Postgres column to numeric(18,2).
    // Scale 2 is a conscious simplification -- the Kuwaiti dinar has 3 decimal
    // places and crypto has more. Revisit when a currency needs it.
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 code: EUR, MDL, USD. No conversion happens anywhere.</summary>
    [MaxLength(3)]
    public required string Currency { get; set; }

    [MaxLength(500)]
    public required string Description { get; set; }

    // Deliberately a plain string, not a Category entity with a foreign key.
    // A model predicts this value in slice 4 and the vocabulary is not known yet.
    // Nullable because "not categorised yet" is a real state until then.
    [MaxLength(100)]
    public string? Category { get; set; }

    // When the row was recorded, which is a different fact from when the money was
    // spent. Set here rather than by a database default so the value is visible
    // without a round trip; the cost is that the application clock is
    // authoritative, not the database one.
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
