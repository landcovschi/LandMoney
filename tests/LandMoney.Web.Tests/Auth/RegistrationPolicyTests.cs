using LandMoney.Web.Auth;

namespace LandMoney.Web.Tests.Auth;

/// <summary>Who may create an account, which is the one decision in registration.</summary>
// A pure function over a record, so this is the part of #52's sign-up that can be
// asserted without a database -- and it is the part with a rule in it. What
// UserManager then does with a valid code is Identity's business and is verified
// by hand against the compose Postgres.
public class RegistrationPolicyTests
{
    // The deployed shape: a code is configured, and it is the only thing that opens
    // the door.
    private static readonly RegistrationPolicy Configured =
        new("let-me-in-please", RequiresInvite: true);

    // Development with nothing configured. Registering on a developer machine must
    // not need a value invented for the occasion.
    private static readonly RegistrationPolicy OpenLocally =
        new(InviteCode: null, RequiresInvite: false);

    // The fail-closed state: a code is required and none is configured. Nobody may
    // register. This is what a deployment with a missing secret lands in, and the
    // reason RequiresInvite is a separate flag rather than being inferred from
    // InviteCode being null -- inferring it would make this indistinguishable from
    // OpenLocally above, which is the exact wrong direction to guess in.
    private static readonly RegistrationPolicy MisconfiguredDeployment =
        new(InviteCode: null, RequiresInvite: true);

    [Fact]
    public void The_configured_code_is_accepted()
    {
        Assert.True(Configured.Accepts("let-me-in-please"));
    }

    [Theory]
    [InlineData("let-me-in")]
    [InlineData("Let-Me-In-Please")]
    [InlineData("let-me-in-please ")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? offered)
    {
        Assert.False(Configured.Accepts(offered));
    }

    // Case and whitespace are part of the secret, which the theory above pins.
    // Trimming or lower-casing "to be helpful" shrinks the space an attacker has to
    // search, and it is the kind of kindness that gets added later by someone
    // fixing a support complaint.

    [Theory]
    [InlineData("anything at all")]
    [InlineData("")]
    [InlineData(null)]
    public void Development_needs_no_code(string? offered)
    {
        Assert.True(OpenLocally.Accepts(offered));
    }

    // The most important one here. A deployment that lost its invite code must
    // refuse everyone rather than let everyone in, and the naive implementation --
    // "if there is no code, do not check it" -- does the opposite. Existing accounts
    // are unaffected; only registration closes.
    [Theory]
    [InlineData("anything at all")]
    [InlineData("")]
    [InlineData(null)]
    public void A_deployment_with_no_code_configured_refuses_everyone(string? offered)
    {
        Assert.False(MisconfiguredDeployment.Accepts(offered));
    }
}
