using System.ComponentModel.DataAnnotations;

namespace LandMoney.Web.Api;

/// <summary>What a client is allowed to send when creating a transaction.</summary>
// A separate type from Transaction, and not merely to satisfy a layering rule.
// Three of the entity's fields are the server's to decide -- Id, CreatedAt, and
// from slice 4 Category, which a model assigns -- and a client that can send a
// field can overwrite it. The second reason weighs more here than in an ordinary
// layered .NET application: the consumer is TypeScript, so this record is the
// schema the client is typed against. Renaming a database column then stops
// being able to break the UI.
//
// Nothing evaluates the attributes below on its own. Minimal APIs bind this type
// and hand it straight to the handler; the DataAnnotations attributes are inert
// metadata until something reads them, which here is ValidationFilter<T>,
// attached to the endpoint in TransactionEndpoints. .NET 10 ships a built-in
// alternative -- AddValidation() with [ValidatableType] -- and it was the first
// choice until the build refused it: that whole API is marked [Experimental]
// (ASP0029) and needs a suppression to compile at all. It would have cost one
// NoWarn line and pinned the project to an API Microsoft reserves the right to
// remove.
public sealed record CreateTransactionRequest
{
    /// <summary>How far ahead of today an entry may be dated. See the field comment.</summary>
    // One day, not zero. The comparison below happens against UTC today, while
    // OccurredAt is a plain date with no zone -- so someone typing at 01:00 on
    // the 20th in UTC+3 is submitting the 20th while the server still calls it
    // the 19th, and a strict "not after today" rejects a correct entry. A day of
    // slack absorbs every real offset (UTC-12 to UTC+14) and still catches the
    // mistakes this rule is for: a typed year, a month that has not happened.
    // This is the same day-boundary problem #17 settled in storage, arriving
    // again in validation -- it does not go away, it only moves.
    public const int MaxDaysAhead = 1;

    /// <summary>The day the money was spent. Sent as "2026-08-19", no time, no zone.</summary>
    // `required` is doing validation work that no attribute can do here.
    // System.Text.Json enforces required members while deserialising, so a body
    // that omits this is rejected during binding. Drop it and a missing date
    // binds quietly to 0001-01-01 and a missing amount to 0, which [Required]
    // cannot object to: after binding, a non-nullable value type is never
    // absent, only default. The alternative is DateOnly? plus [Required], which
    // reports the error more prettily and makes every read site deal with a null
    // that cannot occur.
    [NotFarInFuture(MaxDaysAhead)]
    public required DateOnly OccurredAt { get; init; }

    /// <summary>A positive amount with at most two decimal places.</summary>
    // The ceiling is not decorative: it is exactly what numeric(18,2) holds.
    // Without it an oversized amount reaches Postgres and comes back as a 500
    // from a numeric field overflow; with it the client gets a 400 naming the
    // field. Validation limits are best kept equal to the column's, so the
    // database never has to be the one to say no.
    //
    // ParseLimitsInInvariantCulture matters because those bounds are strings
    // parsed at runtime with the current culture, and a machine set to Romanian
    // or German reads "0.01" as 1. The bug is invisible on an en-US developer
    // machine and appears only where the container's locale differs.
    [Range(typeof(decimal), "0.01", "9999999999999999.99",
        ParseLimitsInInvariantCulture = true,
        ErrorMessage = "Amount must be between {1} and {2}.")]
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 code: EUR, MDL, USD. Stored as sent, uppercased by the handler.</summary>
    // The regular expression carries the floor and the ceiling together, so "E",
    // "" and "EU1" are all refused. StringLength(3, MinimumLength = 3) would do
    // the length half; it would accept "1$x".
    [RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "Currency must be a three-letter ISO 4217 code.")]
    public required string Currency { get; init; }

    /// <summary>What the money was spent on. This is the text the categorizer reads in slice 4.</summary>
    // [Required] rather than only StringLength(MinimumLength = 1) because
    // RequiredAttribute trims before it checks, so a description of three spaces
    // is refused. MinimumLength on its own counts them as content.
    [Required(AllowEmptyStrings = false)]
    [StringLength(500, MinimumLength = 1)]
    public required string Description { get; init; }
}

/// <summary>One transaction as the API reports it.</summary>
// Amount travels as a JSON number (12.34), decided in #3. Not luck: JSON numbers
// are IEEE 754 doubles on the JavaScript side, and a double round-trips any
// decimal of at most 15 significant digits exactly -- these are personal
// purchases in numeric(18,2), so the guarantee holds with room to spare. It
// stops holding if a currency ever needs a third decimal place, or if amounts
// grow past a quadrillion; neither is on this roadmap. A string would be immune
// and would make every sum in React an explicit parse, for a risk that is not
// present.
//
// OccurredAt travels as "2026-08-19" and CreatedAt as ISO 8601 with an offset,
// both of which System.Text.Json produces without configuration. The trap is on
// the client and belongs in #6: new Date("2026-08-19") parses as UTC midnight,
// so anyone west of UTC renders the day before. The date is already a plain
// string in the shape a human reads -- display it, do not construct a Date.
public sealed record TransactionResponse(
    Guid Id,
    DateOnly OccurredAt,
    decimal Amount,
    string Currency,
    string Description,
    string? Category,
    DateTimeOffset CreatedAt);
