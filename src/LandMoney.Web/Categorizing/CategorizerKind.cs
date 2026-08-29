namespace LandMoney.Web.Categorizing;

/// <summary>Why the categorizer was asked, as one word.</summary>
// #67. Until this issue there was one caller and the question never arose: every
// call to the categorizer was one transaction being written, so "how many calls
// were there" and "how many transactions got a category" were the same number.
// A suggestion shown while a description is typed breaks that, and it breaks it
// in the direction that matters -- previews are the majority of calls from here
// on, and against the model each one is a charge.
//
// So it is a dimension rather than a second set of counters: the outcomes,
// the percentiles and the log lines are the same nine things whichever path
// asked, and #64's rule holds unchanged -- a value that reaches a metric tag is
// a closed vocabulary this application owns, never text from elsewhere.
//
// Bounded at two, and that is what makes tagging it safe. The trap #64 names is
// cardinality: a dimension whose values come from data multiplies the number of
// time series for ever. Two constants cannot.
public static class CategorizerKind
{
    /// <summary>A transaction being written. The answer is stored.</summary>
    public const string Save = "save";

    /// <summary>A description being typed. Nothing is written and nothing is stored.</summary>
    // The word is deliberately not "suggestion": every call to this service is a
    // suggestion, and #64 already spends `suggested` on the outcome of one. What
    // separates these two is whether anything came of it.
    public const string Preview = "preview";

    /// <summary>Both, in the order a summary reads them out.</summary>
    public static readonly IReadOnlyList<string> All = [Save, Preview];
}
