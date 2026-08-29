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
//
// Every property below carries [Display], and three of the four name themselves.
// That repetition is the point. The sentence a client reads is part of this API's
// contract and the C# property name is not, so leaving the three implicit would
// make them right by coincidence: renaming Description to Note would quietly
// rewrite a message shown under an input still labelled Description, and nothing
// would report it. Naming all four also puts the messages and the form's labels
// side by side as one list. What lost: [Display] on OccurredAt alone -- the
// smaller diff, and it leaves a rule that applies to one property with no way to
// tell whether the other three were considered or forgotten.
public sealed record CreateTransactionRequest
{
    /// <summary>How far ahead of today an entry may be dated. See the field comment.</summary>
    // One day, not zero. The comparison happens against UTC today, while
    // OccurredAt is a plain date with no zone -- so someone typing at 01:00 on
    // the 20th in UTC+3 is submitting the 20th while the server still calls it
    // the 19th, and a strict "not after today" rejects a correct entry. A day of
    // slack absorbs every real offset (UTC-12 to UTC+14) and still catches the
    // mistakes this rule is for: a typed year, a month that has not happened.
    // This is the same day-boundary problem #17 settled in storage, arriving
    // again in validation -- it does not go away, it only moves.
    public const int MaxDaysAhead = 1;

    /// <summary>How far behind today an entry may be dated. See the field comment.</summary>
    // Five years, added in review: the future bound alone caught a mistyped year
    // in one direction only, so 2062 was refused while 1900 was stored happily.
    // A rule that exists because a hand-typed year goes wrong should not care
    // which way it went.
    //
    // Five rather than something larger, because this is spending typed weekly by
    // one person: entries older than that are not something this application is
    // for. It is deliberately tight enough to catch the near miss as well as the
    // absurd one -- 2026 mistyped as 2016 is ten years back and refused, where a
    // ten-year bound would have waved it through.
    //
    // This is the number to revisit first if CSV import of old statements ever
    // arrives, since that is the one feature on the roadmap that would legitimately
    // post dates from further back.
    public const int MaxYearsBehind = 5;

    /// <summary>The day the money was spent. Sent as "2026-08-19", no time, no zone.</summary>
    // `required` is doing validation work that no attribute can do here.
    // System.Text.Json enforces required members while deserialising, so a body
    // that omits this is rejected during binding. Drop it and a missing date
    // binds quietly to 0001-01-01 and a missing amount to 0, which [Required]
    // cannot object to: after binding, a non-nullable value type is never
    // absent, only default. The alternative is DateOnly? plus [Required], which
    // reports the error more prettily and makes every read site deal with a null
    // that cannot occur.
    //
    // The label above this input says Date, and until #29 the message under it
    // said OccurredAt. ValidationContext.DisplayName prefers [Display] over the
    // member name, so this one line fixes the message everywhere and
    // PlausibleDateAttribute, DecimalScaleAttribute and ValidationFilter are all
    // untouched.
    //
    // What it does not change, and the reason it is safe: the dictionary key.
    // ValidationFilter builds that from ValidationResult.MemberNames through
    // JsonNamingPolicy.CamelCase, so it stays "occurredAt" -- which is what the
    // input's name attribute is, and what puts the sentence beside this field
    // instead of in the form-level banner. A [Display] that reached the key would
    // move every message to the top of the form in silence.
    //
    // Translating on the client was the alternative and is refused: the message
    // would then exist twice, in two languages, and the server would keep sending
    // the untranslated one to everything that is not this form -- so curl would
    // report a sentence the UI never shows. What is wrong with a request is the
    // server's to say.
    [Display(Name = "Date")]
    [PlausibleDate(MaxDaysAhead, MaxYearsBehind)]
    public required DateOnly OccurredAt { get; init; }

    /// <summary>A positive amount with at most two decimal places, matching numeric(18,2).</summary>
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
    //
    // {0} rather than the word Amount typed into the sentence. RangeAttribute
    // formats its message with the display name first and the two bounds after
    // it, so this now follows [Display] the way the two custom attributes above
    // already do -- and the message stops being correct only because the property
    // and the label happen to agree.
    [Display(Name = "Amount")]
    [Range(typeof(decimal), "0.01", "9999999999999999.99",
        ParseLimitsInInvariantCulture = true,
        ErrorMessage = "{0} must be between {1} and {2}.")]
    // [Range] bounds the magnitude and says nothing about the precision, which is
    // the gap this closes: numeric(18,2) accepts a third decimal place and rounds
    // it away without complaint, so a 201 built from the in-memory entity reported
    // 12.345 while the row held 12.35. Found in review of #19.
    [DecimalScale(2)]
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 code: EUR, MDL, USD. Stored as sent, uppercased by the handler.</summary>
    // The regular expression carries the floor and the ceiling together, so "E",
    // "" and "EU1" are all refused. StringLength(3, MinimumLength = 3) would do
    // the length half; it would accept "1$x".
    [Display(Name = "Currency")]
    [RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "{0} must be a three-letter ISO 4217 code.")]
    public required string Currency { get; init; }

    /// <summary>What the money was spent on. This is the text the categorizer reads in slice 4.</summary>
    // [Required] rather than only StringLength(MinimumLength = 1) because
    // RequiredAttribute trims before it checks, so a description of three spaces
    // is refused. MinimumLength on its own counts them as content.
    //
    // Neither rule here spells its own message, so both take the framework's
    // defaults -- and those are built from the display name too ("The Description
    // field is required."). [Display] is therefore doing work on this property
    // as well, not merely restating the name for symmetry.
    [Display(Name = "Description")]
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
//
// CategorySource joined it in #63, and it is the field that makes the column
// earn itself. The value has been written since #59 and read by nobody: a
// correction and a guess were the same thing on screen, and after a week nothing
// could tell them apart. It is `string?` and not an enum for the same reason
// ImportRowProblem.Outcome is a string on the client -- "rules" and "model" are
// another process's words arriving over HTTP, and a closed C# enum here would be
// a compile-time promise about what the categorizer sends.
//
// The invariant it carries, established in #59 and checked against the running
// database: a source exists exactly when a category does. Both null together, or
// neither.
public sealed record TransactionResponse(
    Guid Id,
    DateOnly OccurredAt,
    decimal Amount,
    string Currency,
    string Description,
    string? Category,
    string? CategorySource,
    DateTimeOffset CreatedAt);

/// <summary>What a client is allowed to send when correcting a category.</summary>
// One field, and that is the decision rather than a consequence of there being
// nothing else to change. #63: do not send the whole transaction back to save one
// field. A PATCH that accepted an amount would be a way to overwrite money with a
// stale value from a screen somebody left open -- the row is read, edited
// somewhere else, and then written back in full from the older copy. A request
// type that cannot carry an amount cannot lose one.
//
// `required string?` rather than `string?`, and the two words are doing different
// jobs. System.Text.Json enforces `required` while binding, so a body of `{}` is a
// 400 from the binder before this type reaches the handler; the `?` is what makes
// `{"category": null}` legal, which is how a category is cleared. Without the
// first, absent and null would be the same request and "clear it" would be
// indistinguishable from "you forgot the field" -- the usual PATCH ambiguity,
// answered here by the serializer rather than by a JsonElement and a hand-written
// check for JsonValueKind.Undefined.
//
// Nothing here names a source. The endpoint decides that, and it decides "human"
// every time: a client that could send its own source could file a guess as a
// correction, which is precisely the distinction this issue exists to record.
public sealed record UpdateCategoryRequest
{
    [Display(Name = "Category")]
    [KnownCategory]
    public required string? Category { get; init; }
}

/// <summary>What a client may ask a category about, before there is a transaction. #67.</summary>
// The three fields the categorizer reads, and no date: the day money was spent
// tells a predictor nothing, and a field an endpoint does not use is a field a
// client can be refused for getting wrong. That absence is the whole reason this
// is not CreateTransactionRequest -- a mistyped year would otherwise stop the
// suggestion appearing, for a reason that has nothing to do with the description.
//
// **The rules below are copied from CreateTransactionRequest and that is a
// decision rather than an oversight.** They exist here for a different reason than
// they exist there: on the create path they protect the column, and here they keep
// the outbound request inside what `CategorizeRequest` in contracts.py accepts --
// `amount` is `Field(gt=0, max_digits=18, decimal_places=2)` on that side, so an
// amount this endpoint waved through would come back as a 422 the user cannot see
// and cannot act on. Two contracts that happen to agree, which is the same call
// CategorizerContracts.cs makes for the same three fields one hop further out.
//
// What lost: pulling the bounds out into shared constants both records reference.
// It removes the literal duplication and not the risk, because what actually
// drifts is *which attributes are present* -- a rule added to the create path and
// forgotten here -- and a shared string does nothing about that. It also edits the
// file where these rules are decided for the benefit of a file that consumes them.
// CategorySuggestionRequestTests reads both types by reflection and fails when they
// disagree, which is the same answer CategoriesTests gives to the same problem.
//
// There is no ownership or rate limiting on any of this, and it is worth saying
// where it will be looked for: a signed-in caller can ask for as many suggestions
// as it likes, and against a model each one is a charge. What stands between this
// and a bill is the client's debounce and minimum length, which is a control in the
// wrong place -- acceptable while registration needs an invite code and the
// deployed categorizer runs the rules (#61), and the first thing to revisit when
// either stops being true.
public sealed record CategorySuggestionRequest
{
    [Display(Name = "Amount")]
    [Range(typeof(decimal), "0.01", "9999999999999999.99",
        ParseLimitsInInvariantCulture = true,
        ErrorMessage = "{0} must be between {1} and {2}.")]
    [DecimalScale(2)]
    public required decimal Amount { get; init; }

    [Display(Name = "Currency")]
    [RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "{0} must be a three-letter ISO 4217 code.")]
    public required string Currency { get; init; }

    [Display(Name = "Description")]
    [Required(AllowEmptyStrings = false)]
    [StringLength(500, MinimumLength = 1)]
    public required string Description { get; init; }
}

/// <summary>What the suggestion endpoint answers. Always a 200. #67.</summary>
// Two nullable fields carrying three states, and the rule is that **Source says
// something answered**:
//
//   {"category":"groceries","source":"rules"}  a suggestion
//   {"category":null,"source":"rules"}         it answered, and had no idea
//   {"category":null,"source":null}            nothing answered
//
// The middle one is why this is not simply the category. #67 asks for "no idea" to
// be shown, because it is a normal answer on roughly a third of the labelled set
// and a screen that shows nothing for it is a screen that looks broken every third
// transaction -- while a categorizer that is not running has to be invisible, since
// there is nothing the person typing could do about it. On the wire from the Python
// service both are the same null; CategorizerAnswer is where they are separated.
//
// A status code was the alternative -- 200 for an answer, 503 for no categorizer --
// and it lost on what it would mean. Nothing here failed: this endpoint answered
// the question it was asked, and the answer is that there is no suggestion. A 5xx
// would also put a red line in the browser's console for the ordinary state of an
// application whose categorizer is optional by design.
//
// It does not carry the transaction back. The client already has every field, it
// sent them, and echoing them would invite a screen that trusts this response for
// something other than the one word it is for.
public sealed record CategorySuggestionResponse(string? Category, string? Source);
