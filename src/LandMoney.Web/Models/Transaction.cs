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

    /// <summary>How long an owner id may be. Public because the tests assert against it.</summary>
    // 200 rather than a Guid column, because the value is not this application's
    // to shape: it is the `sub` claim of whatever provider signs the user in.
    // Entra sends a Guid, Google sends 21 digits, and the specification puts no
    // upper bound on either. 200 is the length the ASP.NET Core Identity schema
    // uses for the same job, which is the only precedent worth copying here.
    public const int OwnerIdMaxLength = 200;

    /// <summary>Who this row belongs to: the `sub` claim of the signed-in user.</summary>
    // Nullable, and that is a migration decision rather than a modelling one.
    // The column arrives on a table that already holds rows, and nothing in the
    // database knows who entered them -- the fact was never recorded, because
    // until #52 there was nobody to record. A non-nullable column would need a
    // value invented for those rows at migration time, and any value invented
    // there is a claim about ownership that is not true.
    //
    // So null means "entered before there was an owner", and the query filter in
    // AppDbContext makes such a row invisible to everyone rather than visible to
    // anyone. That is deliberately the safe half of the trade: the rows are still
    // in the table, and step 15 of docs/deploy-azure.md is the one UPDATE that
    // hands them to the account that actually typed them, run once, after the
    // first sign-in has produced a subject id to hand them to.
    //
    // Not a foreign key to a users table, and there is no users table. A row
    // needs to know which subject owns it; nothing in this application needs to
    // list users, name them or relate anything else to them. The day something
    // does -- a shared household budget is the obvious one -- is the day this
    // becomes an FK, and it is a migration rather than a redesign.
    [MaxLength(OwnerIdMaxLength)]
    public string? OwnerId { get; set; }

    // A date, not an instant, deliberately. timestamptz stores a moment, and a
    // moment only becomes a day once a timezone is applied -- 01:00 in UTC+3 is
    // stored as 22:00 UTC on the day before, so grouping by day gives a different
    // answer depending on the zone it is grouped in. A human types this field by
    // hand and does not remember the minute; dropping the time removes the
    // question instead of answering it. CreatedAt below keeps full precision,
    // because that one is produced by a machine.
    /// <summary>The day the money was spent. Typed by a human; no time of day.</summary>
    public DateOnly OccurredAt { get; set; }

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
    /// <summary>How long a category may be. Public because CategorizerClient checks against it.</summary>
    // A const rather than the literal in the attribute, added in #39. The
    // categorizer is a separate process with its own vocabulary, so an answer
    // longer than the column is a thing that can happen -- and it would throw in
    // SaveChangesAsync, losing the user's transaction to a failed guess about it.
    // CategorizerClient refuses such an answer, and reads the limit from here so
    // the two cannot drift. Same shape as CreateTransactionRequest.MaxDaysAhead.
    public const int CategoryMaxLength = 100;

    [MaxLength(CategoryMaxLength)]
    public string? Category { get; set; }

    /// <summary>How long a category source may be. Public because the tests assert against it.</summary>
    // 20 rather than 100, and the difference is the point: Category holds whatever
    // vocabulary the categorizer has, and this holds a value from a list this
    // application owns -- `rules`, `model`, and `human` when #F lands. A wide
    // column would invite a sentence.
    public const int CategorySourceMaxLength = 20;

    /// <summary>Which producer wrote <see cref="Category"/>: `rules`, `model`, later `human`.</summary>
    // The open decision with a deadline from CLAUDE.md, closed here because #59
    // puts a second producer behind the categorizer's port. Until now every
    // category in this table came from the rules by construction -- nothing else
    // could write one -- so the provenance was recoverable from the date. The
    // moment a model can answer, that stops holding **retroactively for the rows
    // written in between**, and no migration can recover a fact that was never
    // recorded. Which is why this column had to arrive in the same change as the
    // adapter and before it was switched on, rather than "when we start
    // comparing": the comparison is the thing that needs the data.
    //
    // Nullable, for the same reason OwnerId is: the column arrives on a table that
    // already holds rows. The migration backfills the rows that *have* a category
    // to `rules`, which is provably true rather than merely defensible -- the
    // request contract has never offered the field, CategorizerClient is the only
    // writer, and CLAUDE.md forbade a model call until a baseline was scored. Rows
    // with no category keep a null source, because "nothing wrote a category" has
    // no producer to name.
    //
    // Not an enum, and not a lookup table. An enum here would be a C# type mapped
    // to a database value, so adding `human` in #F becomes a schema conversation
    // rather than a string; and the honest reason a lookup table is wrong is that
    // this column records what happened, not what is allowed. The set is closed
    // where it matters -- CategorizerClient refuses a source it cannot store, and
    // the endpoint names `rules`/`model` nowhere: the value is whatever the
    // implementation that answered called itself, which is #59's rule that the
    // truth about which code ran must not live in a different file from the code
    // that ran.
    [MaxLength(CategorySourceMaxLength)]
    public string? CategorySource { get; set; }

    /// <summary>How many times the categorizer has been asked about this row, or null when nothing is owed.</summary>
    // #92. The save no longer waits for a category: CreateAsync writes the row,
    // answers 201, and CategorizerSweep fills the column in afterwards. This is
    // the whole of the work-tracking that change needs, and the shape of it is
    // the decision worth reading.
    //
    // **Null means nothing is owed. A number means something is, and how many
    // attempts it has cost.** One column doing both jobs rather than a `pending`
    // flag beside a counter, because two columns can disagree -- `pending = false`
    // with three attempts recorded is a state nothing should be able to produce,
    // and the only way to make that impossible is to not have somewhere to write
    // it.
    //
    // **The alternative was no column at all: sweep `WHERE category IS NULL`.**
    // It needs no migration and it is wrong, for a reason #63 wrote down and
    // deferred to exactly this change: clearing a category in the interface sets
    // both `category` and `category_source` to null, so a row somebody
    // deliberately cleared is indistinguishable from one nothing has ever
    // touched. A sweep keyed on that predicate re-predicts over a person's "I do
    // not know either" -- and #63's own text says to reopen the question "the day
    // something re-categorises existing rows". This is that day, and an explicit
    // marker answers it without changing what clearing means: a cleared row was
    // never marked owed, so the sweep cannot see it. The rows that predate this
    // column are null for the same reason and are equally out of reach, which is
    // correct -- nothing asked for them to be categorised.
    //
    // **Counted, rather than a plain flag, because a retry that never stops is a
    // bill.** Once the model is behind the port (#87) every attempt that reaches
    // it is about 0.62 US cents, so a row the service keeps answering unusably
    // would bill for ever at one call per sweep. The cap is configuration;
    // CategorizerSweep is where it is applied and where the rule about which
    // outcomes increment this lives -- the short version is that an attempt is
    // only charged for when something was actually sent.
    //
    // A row that reaches the cap keeps a non-null value here and never becomes
    // null, so "we tried and gave up" stays queryable -- `WHERE
    // categorization_attempts >= 20` -- rather than being erased into the same
    // state as a row nobody ever owed anything about. Reviving one is an UPDATE
    // setting this back to 0; there is deliberately no endpoint for it, because
    // nothing has yet needed one twice.
    public int? CategorizationAttempts { get; set; }

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
