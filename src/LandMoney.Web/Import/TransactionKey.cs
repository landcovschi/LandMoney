namespace LandMoney.Web.Import;

/// <summary>What makes two transactions the same one for the purposes of an import.</summary>
// #62 asks the re-import question and refuses to let it be answered by silence.
// The answer taken: a row matching an existing one on all four of these is skipped
// and named in the response.
//
// **What that costs, said out loud because the response says it too:** two
// identical real purchases on one day -- two 38 MDL espressos, same shop, same
// description -- are one row after an import. The alternative was importing
// everything and reporting a count, which never loses a real repeat and silently
// doubles the table when a file is sent twice. Neither is free; this one fails in
// the direction that is visible and correctable, because the response names the
// line it skipped and the form is still there to add it by hand.
//
// A record struct rather than a tuple so the four members have names at every use
// site, and so this file exists to hold the paragraph above.
//
// **Decimal equality is load-bearing here and is not obvious.** Postgres returns
// numeric(18,2) as 12.50 -- scale 2 -- while a CSV may well say 12.5, scale 1.
// Those are different bit patterns. The generated Equals uses
// EqualityComparer<decimal>, which is decimal.Equals and compares values rather
// than representations, and decimal.GetHashCode is normalised to match it, so both
// sides of a HashSet lookup agree. If that were not true the failure would be a
// silent double-import, which is why there is a test naming exactly this.
//
// String members compare ordinally, which is what EqualityComparer<string> does.
// That is deliberate rather than incidental: a case-insensitive description
// comparison would merge two rows a person wrote differently on purpose, and this
// is a duplicate check, not a search.
public readonly record struct TransactionKey(
    DateOnly OccurredAt,
    decimal Amount,
    string Currency,
    string Description);
