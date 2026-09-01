using LandMoney.Web.Categorizing;

namespace LandMoney.Web.Tests.Categorizing;

/// <summary>Which outcomes are charged against the sweep's retry ceiling. #92.</summary>
// The whole of the bill trap in one boolean. A row is retried until a cap because
// every attempt that reaches the model is about 0.62 US cents (#87); the cap is
// only honest if what it counts is attempts that could have been charged for,
// rather than attempts that were made.
//
// Getting this wrong is invisible in both directions and expensive in one. Charging
// too much abandons rows during an outage that cost nothing -- recoverable, and
// visible as rows that stay uncategorised. Charging too little retries a row that
// bills every time, for ever, which is #92's fourth trap and shows up as a bill.
public class CategorizerOutcomeTests
{
    // Nothing was sent. `not-configured` never opened a socket, and `unreachable`
    // is a refusal, a reset or a name that does not resolve -- no HTTP request
    // completed, so nothing ran at the other end and nothing was billed.
    [Theory]
    [InlineData(CategorizerOutcome.NotConfigured)]
    [InlineData(CategorizerOutcome.Unreachable)]
    public void An_attempt_that_sent_nothing_is_not_charged(string outcome)
    {
        Assert.False(CategorizerOutcome.CountsAgainstTheCap(outcome));
    }

    // Everything else got a response, or may have. The interesting member is
    // `timeout`: #64 records that a call the model answers at seven seconds is
    // billed and still arrives here as a timeout, and #39 measured that a *stopped
    // container* also arrives here rather than as `unreachable`, because the SYN
    // goes unanswered instead of being refused. So this branch genuinely cannot
    // tell a free failure from a paid one, and it is charged -- because between "an
    // outage abandons some rows, visibly and recoverably" and "a slow model bills
    // for ever, silently", the first is the one to choose.
    [Theory]
    [InlineData(CategorizerOutcome.Suggested)]
    [InlineData(CategorizerOutcome.Abstained)]
    [InlineData(CategorizerOutcome.Refused)]
    [InlineData(CategorizerOutcome.Timeout)]
    [InlineData(CategorizerOutcome.Unreadable)]
    [InlineData(CategorizerOutcome.Unusable)]
    [InlineData(CategorizerOutcome.Abandoned)]
    public void An_attempt_that_may_have_reached_the_model_is_charged(string outcome)
    {
        Assert.True(CategorizerOutcome.CountsAgainstTheCap(outcome));
    }

    // So that a tenth outcome cannot be added without somebody deciding which side
    // of the ceiling it falls on. Without this the default is "charged", silently,
    // which is the safe direction for a bill and the wrong direction for a row --
    // and either way it would be a decision nobody made.
    [Fact]
    public void Every_outcome_is_accounted_for_on_one_side_or_the_other()
    {
        string[] free = [CategorizerOutcome.NotConfigured, CategorizerOutcome.Unreachable];

        string[] charged =
        [
            CategorizerOutcome.Suggested,
            CategorizerOutcome.Abstained,
            CategorizerOutcome.Refused,
            CategorizerOutcome.Timeout,
            CategorizerOutcome.Unreadable,
            CategorizerOutcome.Unusable,
            CategorizerOutcome.Abandoned,
        ];

        Assert.Equal(
            CategorizerOutcome.All.OrderBy(outcome => outcome, StringComparer.Ordinal),
            free.Concat(charged).OrderBy(outcome => outcome, StringComparer.Ordinal));
    }
}

/// <summary>The three reasons the categorizer is asked. #92 adds the third.</summary>
public class CategorizerKindTests
{
    // A third word rather than reusing `save`, and the summary is where the
    // difference is paid for. Until #92 every `save` call happened inside the
    // request that wrote the row; from here none do. Reusing the word would leave
    // #64's summary reporting the same number for a different event -- the one
    // failure a closed vocabulary exists to prevent -- and would throw away the
    // signal that this change took at all.
    [Fact]
    public void The_sweep_is_counted_apart_from_the_save_it_replaced()
    {
        Assert.Equal(
            [CategorizerKind.Save, CategorizerKind.Preview, CategorizerKind.Sweep],
            CategorizerKind.All);
    }

    // Still bounded, which is what makes tagging a metric with it safe. #64's first
    // trap is cardinality: a dimension whose values come from data multiplies the
    // number of time series for ever. Three constants cannot, and this fails if
    // somebody ever tags this dimension with something a service sent.
    [Fact]
    public void The_kinds_are_this_applications_own_words_and_there_are_three()
    {
        Assert.Equal(3, CategorizerKind.All.Count);
        Assert.Equal(CategorizerKind.All.Count, CategorizerKind.All.Distinct(StringComparer.Ordinal).Count());
    }
}
