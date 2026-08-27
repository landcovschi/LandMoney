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
