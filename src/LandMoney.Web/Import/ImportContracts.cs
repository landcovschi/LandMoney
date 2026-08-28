namespace LandMoney.Web.Import;

/// <summary>What happened to one row that did not simply import.</summary>
// Skipped and Rejected are different facts and the client shows them differently:
// a rejected row is a mistake in the file to go and fix, a skipped one is a row
// that was already there and needs nothing done about it.
//
// Plain strings rather than an enum, for the reason Transaction.CategorySource
// already gives: System.Text.Json writes an enum as a number by default, so the
// wire format would depend on a serializer setting written somewhere else, and
// adding a third outcome should be a string rather than a schema conversation.
public static class ImportOutcomes
{
    public const string Skipped = "skipped";
    public const string Rejected = "rejected";
}

/// <summary>One row that was not imported, and why. LineNumber is the file's own.</summary>
public sealed record ImportRowProblem(int LineNumber, string Outcome, string Reason);

/// <summary>What an import did.</summary>
// #62: "Report what was rejected and why, per row. An import that silently drops
// four lines is worse than one that refuses."
//
// The four counts always add up -- Rows == Imported + Skipped + Rejected -- and
// they are sent even when Problems is truncated, so the summary is never the thing
// that is missing. That is what makes the truncation safe: the numbers are exact,
// and only the list of individual explanations is cut.
public sealed record ImportResponse(
    int Rows,
    int Imported,
    int Skipped,
    int Rejected,
    IReadOnlyList<string> IgnoredColumns,
    bool ProblemsTruncated,
    IReadOnlyList<ImportRowProblem> Problems);
