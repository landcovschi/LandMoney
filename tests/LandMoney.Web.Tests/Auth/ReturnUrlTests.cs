using LandMoney.Web.Auth;

namespace LandMoney.Web.Tests.Auth;

/// <summary>That /auth/login cannot be used to send someone somewhere else.</summary>
// An open redirect on a sign-in path is the one that is worth having, from an
// attacker's point of view: the victim follows a link on the real site, really
// does sign in to the real provider, and is then handed to a page that asks for
// the password again. Nothing about the URL bar contradicts it until the last
// step.
public class ReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/transactions")]
    [InlineData("/a/b?c=d#e")]
    public void A_path_on_this_site_is_kept(string returnUrl)
    {
        Assert.Equal(returnUrl, AuthEndpoints.LocalOrRoot(returnUrl));
    }

    [Theory]
    // The obvious one.
    [InlineData("https://example.invalid")]
    [InlineData("http://example.invalid/path")]

    // Protocol-relative: no scheme, so it looks like a path and is a different
    // host. This is the one a check written as "must not start with http" misses.
    [InlineData("//example.invalid")]

    // The same trick with a backslash. It starts with '/' and would pass a naive
    // first-character test; several browsers normalise the backslash to a forward
    // slash and treat the whole thing as protocol-relative.
    [InlineData("/\\example.invalid")]

    // Not a redirect target at all, and worth pinning: a scheme that executes.
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_could_leave_this_site_becomes_the_root(string? returnUrl)
    {
        Assert.Equal("/", AuthEndpoints.LocalOrRoot(returnUrl));
    }

    // A single slash is a legal destination and must not be caught by the
    // two-character check above -- the guard reads returnUrl[1], and a string of
    // length one has no such index. This is the off-by-one that would turn every
    // sign-in into an IndexOutOfRangeException.
    [Fact]
    public void The_root_itself_is_not_mistaken_for_a_protocol_relative_url()
    {
        Assert.Equal("/", AuthEndpoints.LocalOrRoot("/"));
    }
}
