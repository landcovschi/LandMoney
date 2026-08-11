using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Models;

/// <summary>
/// One item of spending, entered by hand.
/// </summary>
public class Transaction
{
    // Guid rather than an int identity, deliberately: the id exists before the row
    // does, so the client can send it and there is no sequence to collide when rows
    // arrive from more than one source -- which the Python categorizer may do in
    // slice 4. The cost is that a random v4 GUID scatters inserts across the
    // primary-key index instead of appending; unmeasurable at a few thousand
    // personal transactions, and the reason the trade-off exists at all.
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

    // ISO 4217 codes are exactly three characters, so the floor matters as much as
    // the ceiling: [MaxLength(3)] lets "E" and "" through. Same column type either
    // way -- this is the validation attribute, not the schema.
    /// <summary>ISO 4217 code: EUR, MDL, USD. No conversion happens anywhere.</summary>
    [StringLength(3, MinimumLength = 3)]
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
    // authoritative, not the database one. Once this runs in Container Apps the
    // container and the database may sit in different regions, and the two clocks
    // can disagree -- a database default (now()) is the usual answer to that.
    //
    // This initializer also runs when EF Core materialises a row from the database
    // and is then overwritten by the stored value. Harmless, but it is not doing
    // what it looks like it is doing on the read path.
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
