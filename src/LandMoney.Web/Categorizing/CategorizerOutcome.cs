namespace LandMoney.Web.Categorizing;

/// <summary>What happened to one call to the categorizer, as one word.</summary>
// #64. A closed vocabulary, for the same reason `Categories` is one: this word is
// a metric dimension and a log field, and both are only worth having if the same
// event is called the same thing every time. Before this, the branches of
// `CategorizerClient` each wrote a sentence, so "how often is it unreachable"
// meant matching prose that nobody promised would stay still.
//
// The list is deliberately longer than the four `catch` blocks #64 counted. Three
// of the outcomes below never raise anything at all -- an abstention, a refused
// status and an answer that breaks the contract are all ordinary returns -- and
// leaving them unnamed is exactly how an abstention comes to be counted as a
// failure, which is the third thing the issue asks to be kept apart.
//
// **Nothing here is a message and nothing here carries data from a transaction.**
// These are the only values that ever reach a metric tag, which is what keeps the
// cardinality bounded and keeps the user's own spending out of a dimension --
// #64's first trap, and the reason a description or an amount must never be
// tagged even when it would be convenient.
public static class CategorizerOutcome
{
    /// <summary>A usable category came back. The only outcome that stores anything.</summary>
    public const string Suggested = "suggested";

    /// <summary>A 200 saying "I do not know". The normal case, and never an error.</summary>
    // The rules decline on roughly a third of the labelled set, so this is the
    // baseline working as designed. It has to be visibly separate from every
    // failure below, because on the wire an abstention and a dead service are the
    // same `null` -- #64's third acceptance test in one line.
    public const string Abstained = "abstained";

    /// <summary>A 200 whose body this application refuses to store.</summary>
    // A category longer than the column, a source longer than the column, or a
    // category whose producer is not named (#59). All three mean the service broke
    // its own contract, which is a bug on one side or the other rather than a
    // network event -- and they are one word rather than three because the fix is
    // always "read the log line, which names which guard fired".
    public const string Unusable = "unusable";

    /// <summary>A response that was not a success status.</summary>
    public const string Refused = "refused";

    /// <summary>One of the two clocks fired. Not the caller's cancellation.</summary>
    // #64 warns that this is the one easiest to label wrongly: a stopped container
    // leaves the SYN unanswered rather than refusing it, so it arrives here and not
    // as `Unreachable`. That is measured behaviour (#39) rather than a guess, and
    // it is why the two names are kept apart at all.
    public const string Timeout = "timeout";

    /// <summary>Refused, reset, or a name that does not resolve.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>A success status carrying something that is not the contract.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>No categorizer is configured, so nothing was sent.</summary>
    // Counted rather than ignored, and it is the outcome most worth having a name
    // for: it is what the deployed application did on every save between #39 and
    // #61 -- the whole of the bug that issue was opened about -- and nothing
    // anywhere reported it. A number on this line is the difference between "the
    // categorizer answers nothing" and "there is no categorizer".
    public const string NotConfigured = "not-configured";

    /// <summary>The caller went away while the call was in flight.</summary>
    // The one outcome that does not end in a stored transaction: the exception is
    // rethrown, because saving a row for a request whose caller has gone is what
    // the `when` clause in CategorizerClient exists to prevent. Counted because the
    // call was made and paid for, and because a rising number here means requests
    // are being abandoned upstream -- which is a fact about the browser client's
    // timeout, not about the categorizer.
    public const string Abandoned = "abandoned";

    /// <summary>Could this attempt have cost money? #92.</summary>
    // The rule behind the sweep's retry ceiling, and the one place it is written.
    // A row is retried until a cap because every attempt that reaches the model is
    // about 0.62 US cents (#87) and a sweep that retries for ever is a bill with
    // no ceiling -- #92's fourth trap. What the cap must count, then, is attempts
    // that could have been charged for, not attempts that were made.
    //
    // **Two outcomes provably sent nothing.** `not-configured` never opened a
    // socket, and `unreachable` is a refusal, a reset or a name that does not
    // resolve -- no HTTP request completed, so no model ran. Charging the cap for
    // either would abandon rows for the duration of an outage that cost nothing,
    // which is the wrong way round: the row is still owed and the service will
    // come back.
    //
    // **`timeout` is charged, and it is the uncomfortable one.** #64 records that
    // a call the model answers at seven seconds is billed and still reads as a
    // timeout here, and #39 measured that a *stopped container* also arrives here
    // rather than as `unreachable`, because the SYN goes unanswered instead of
    // being refused. So this branch cannot tell a free failure from a paid one.
    // It is counted, because between "an outage abandons some rows, visibly and
    // recoverably" and "a slow model bills for ever, silently", the first is the
    // failure to choose. What it costs is written down on CategorizerSweep.
    //
    // Everything else got a response, so something ran at the other end.
    public static bool CountsAgainstTheCap(string outcome) =>
        outcome is not (NotConfigured or Unreachable);

    /// <summary>Every outcome, in the order a summary reads them out.</summary>
    // Ordered by how much attention each deserves rather than alphabetically:
    // the two normal answers, then the ways it fails, then the two that are not
    // really about the service at all. A summary line reads in this order, so
    // re-sorting this array changes what a log line looks like.
    public static readonly IReadOnlyList<string> All =
    [
        Suggested,
        Abstained,
        Refused,
        Timeout,
        Unreachable,
        Unreadable,
        Unusable,
        NotConfigured,
        Abandoned,
    ];
}
