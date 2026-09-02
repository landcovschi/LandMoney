using System.Text.Json.Serialization;

namespace LandMoney.Web.Categorizing;

/// <summary>What the categorizer is sent. Mirrors CategorizeRequest in contracts.py.</summary>
// Its own record rather than reusing CreateTransactionRequest, although the three
// fields overlap exactly today. Two different contracts happen to agree: one is
// what a browser may send this application, the other is what this application
// sends a service it does not own. Sharing the type would mean a validation
// attribute added for the form silently becoming part of a service call, and a
// field the categorizer starts wanting becoming a field the browser may set.
//
// Amount travels as a JSON number, which System.Text.Json writes from a decimal
// exactly -- 12.34 is "12.34" and not a float's 12.339999999999999857. That
// matters more than usual on this hop: the Python side declares
// `decimal_places=2`, so an amount that arrived through a float would be
// rejected as a 422 rather than merely rounded.
internal sealed record CategorizeRequest(string Description, decimal Amount, string Currency);

/// <summary>What it answers. Mirrors CategorizeResponse in contracts.py.</summary>
// Category is null when the rules abstained -- a normal 200, not an error. See
// the Python type's docstring for why the "unknown" sentinel stops at that
// boundary rather than being served.
//
// Source was read, logged and not stored until #59; it is now written to
// transactions.category_source, which is the open decision with a deadline from
// CLAUDE.md being closed in the same change that put a model behind the port.
// Until then every category in the table came from the rules by construction, so
// the provenance was recoverable from the date -- and that stops being true
// retroactively the moment a second producer runs.
//
// Still `string?` on the wire although the Python side declares a non-optional
// `Source` enum, and that is not laziness. This record describes what may arrive
// from another process, not what that process promises: a body missing the field
// deserialises here rather than throwing, and CategorizerClient decides what to do
// about it. It refuses the whole answer -- see there for why a category whose
// producer cannot be named is exactly what the column above exists to prevent.
//
// JsonPropertyName on both, although JsonSerializerDefaults.Web would match
// these case-insensitively anyway. The attribute is what makes the mapping
// survive someone changing the options, and it puts the wire name where a reader
// comparing this file against contracts.py can see it.
internal sealed record CategorizeResponse(
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("source")] string? Source);


/// <summary>The most rows one batch request may carry. Mirrors MAX_BATCH_ITEMS in contracts.py.</summary>
// #93's second trap: "a year of imports in one request is the same failure the
// per-row loop had, in a nicer coat". The bound is the Python side's -- going over
// it is a 422, which from here reads as the categorizer misbehaving rather than as
// a number in appsettings.json -- so this constant exists to keep that from
// happening rather than to impose a limit of its own.
//
// Written in two languages, and pinned to the other one by
// CategorizerBatchCapTests, which reads contracts.py the way CategoriesTests reads
// categories.py. That is this repository's answer to a constant that cannot live
// in one file: not a comment asking two people to remember, but a test that fails
// naming both.
// Public where the wire records around it are internal, and that is the test
// reaching in rather than a wider surface: this number is a fact about a contract
// written in two languages, and the thing that keeps the two honest has to be able
// to read it. Program.cs records why this assembly has no InternalsVisibleTo.
public static class CategorizerBatch
{
    public const int MaxItems = 100;

    /// <summary>A configured batch size, held inside what one request may carry.</summary>
    // Beside the number rather than on CategorizerSweep, because it is the number
    // that makes it right: going over the cap is a 422 on every single tick, which
    // leaves every row in the batch owed a category and one attempt poorer and reads
    // in a log as the categorizer misbehaving rather than as a number in
    // appsettings.json.
    //
    // Clamped rather than refused at startup, because the failure it prevents is
    // total and the cost of the clamp is a slower sweep. The sweep says so once, in
    // the line it writes when it starts, so a number that was silently held down is
    // still findable.
    //
    // One is the floor. Zero would make every tick a query that claims nothing, which
    // looks exactly like a categorizer that is never reached -- and the setting that
    // turns categorising after the fact off is the interval, which says so when it
    // does.
    public static int HeldWithinOneRequest(int configured) => Math.Clamp(configured, 1, MaxItems);
}

/// <summary>One row of a batch. Mirrors BatchItem in contracts.py.</summary>
// The id is this application's own key for the row, and it is what makes the
// answers keyed rather than positional -- #93's last trap, which is a batch that
// drops a row and shifts every answer after it by one, showing up much later as one
// transaction categorised as its neighbour.
//
// A transaction id is what is sent. Nothing on the Python side depends on that or
// writes it down; it is an opaque string over there, which is the property to keep
// if anything else ever calls this endpoint.
internal sealed record CategorizeBatchItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency);

/// <summary>Many rows in. Mirrors CategorizeBatchRequest in contracts.py.</summary>
internal sealed record CategorizeBatchRequest(
    [property: JsonPropertyName("items")] IReadOnlyList<CategorizeBatchItem> Items);

/// <summary>One answer out. Mirrors BatchAnswer in contracts.py.</summary>
// Every field is nullable, including the id, and that is the same decision the
// single-row CategorizeResponse makes for the same reason: this record describes
// what may arrive from another process, not what that process promises. An answer
// with no id cannot be paired with anything, so the client drops it and says so --
// which is a different and much louder failure than pairing it with whichever row
// happened to be next.
internal sealed record CategorizeBatchAnswer(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("source")] string? Source);

/// <summary>Many answers out. Mirrors CategorizeBatchResponse in contracts.py.</summary>
// **There may be fewer answers than there were items**, which is the Python side's
// contract rather than an accident: a row whose predictor raised is left out so
// that the other ninety-nine keep the answers they were already paid for. A row
// that comes back missing is therefore an expected state here and not a parse
// failure -- see CategorizerClient, which charges it an attempt and leaves it
// owing.
internal sealed record CategorizeBatchResponse(
    [property: JsonPropertyName("answers")] IReadOnlyList<CategorizeBatchAnswer>? Answers);
