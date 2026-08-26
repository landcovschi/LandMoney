using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using LandMoney.Web.Api;
using Microsoft.AspNetCore.Identity;

namespace LandMoney.Web.Auth;

/// <summary>Register, sign in, sign out, and who am I.</summary>
// Hand-written rather than MapIdentityApi<IdentityUser>(), which is one line and
// was the first choice. What it maps is nine endpoints: register, login, refresh,
// confirmEmail, resendConfirmationEmail, forgotPassword, resetPassword,
// manage/info and manage/2fa. Four of those need an email sender this application
// deliberately does not have, so they would answer 200 and do nothing -- an
// endpoint that reports success and sends no mail is worse than one that is not
// there. Its /login also speaks a bearer-token dialect unless asked for cookies.
//
// Three endpoints written out is about sixty lines, uses exactly the same
// UserManager and SignInManager underneath, and every one of them does something.
public static class AuthEndpoints
{
    /// <summary>Registers /api/auth/* and /api/me. Called from Program.cs.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Under /api like everything else the client calls with `fetch`, so a
        // refusal is a status rather than a redirect. There is no page to redirect
        // to: the login form is a React component, served by the same index.html as
        // the rest of the client.
        var group = routes.MapGroup("/api/auth");

        // Anonymous by necessity -- a sign-in that required a sign-in is a locked
        // door with the key inside. AllowAnonymous is written out although nothing
        // on this group requires authorization today, because the day someone adds
        // RequireAuthorization to the group is the day it would silently apply here
        // as well.
        group.MapPost("/register", RegisterAsync)
            .AllowAnonymous()
            .AddEndpointFilter<ValidationFilter<RegisterRequest>>();

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .AddEndpointFilter<ValidationFilter<LoginRequest>>();

        // Anonymous too, and harmless: signing out when already signed out is a
        // no-op. Requiring authorization would answer 401 to someone whose cookie
        // has expired -- which is exactly the person trying to clear it.
        group.MapPost("/logout", LogoutAsync).AllowAnonymous();

        // Not in the group: /api/me reads better than /api/auth/me from the
        // client's side, and it is the only one of the four that needs a session.
        routes.MapGet("/api/me", Me).RequireAuthorization();

        return routes;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        RegistrationPolicy policy,
        UserManager<IdentityUser> users,
        SignInManager<IdentityUser> signIn)
    {
        if (!policy.Accepts(request.InviteCode))
        {
            // One message for "wrong code" and for "no code is configured, so
            // nobody may register". The distinction matters to whoever runs this
            // and not at all to whoever is typing, and telling a stranger which of
            // the two it is tells them whether guessing is worth continuing.
            return Refused("That invite code is not valid.", nameof(request.InviteCode));
        }

        var user = new IdentityUser { UserName = request.UserName };
        var created = await users.CreateAsync(user, request.Password);

        if (!created.Succeeded)
        {
            // Identity's own messages, which are worth showing: "Passwords must be
            // at least 10 characters." and "Username 'x' is already taken." are
            // both things the person can act on. Keyed to the field they are about,
            // so the form can put them beside it the way ValidationFilter<T> does.
            var aboutPassword = created.Errors.Any(
                e => e.Code.Contains("Password", StringComparison.Ordinal));

            return Refused(
                string.Join(" ", created.Errors.Select(e => e.Description)),
                aboutPassword ? nameof(request.Password) : nameof(request.UserName));
        }

        // Signed in immediately, so registering is one step rather than two. The
        // alternative -- create the account, then send them to the login form -- is
        // what an application with email confirmation has to do, and this one has
        // no confirmation to wait for.
        await signIn.SignInAsync(user, isPersistent: true);

        return TypedResults.Ok(Describe(user));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        SignInManager<IdentityUser> signIn,
        UserManager<IdentityUser> users)
    {
        // lockoutOnFailure: true, and it is the argument that matters here. The
        // lockout policy configured in AuthenticationSetup does nothing unless a
        // call opts into it, so with the default `false` those settings would sit
        // in the options object looking like protection while an attacker guessed
        // passwords at whatever rate the network allowed.
        var result = await signIn.PasswordSignInAsync(
            request.UserName,
            request.Password,
            isPersistent: true,
            lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            // Worth telling apart from a wrong password, because the fix is
            // different: waiting. It does confirm the account exists, which is a
            // real leak and the accepted price of not leaving someone staring at
            // "wrong password" while typing the right one.
            return Refused(
                "Too many attempts. This account is locked for a few minutes.",
                nameof(request.Password));
        }

        if (!result.Succeeded)
        {
            // One message for a username that does not exist and for a password
            // that is wrong. Two messages would turn this endpoint into a way of
            // finding out which usernames are real.
            return Refused("That username and password do not match.", nameof(request.Password));
        }

        var user = await users.FindByNameAsync(request.UserName);

        return TypedResults.Ok(Describe(user!));
    }

    // POST, unlike the OpenID Connect version this replaced, where sign-out had to
    // be a link a browser could follow to the provider. There is no provider now,
    // so it is an ordinary API call -- and a GET that changes state can be
    // triggered by any page that can make this browser fetch an image.
    private static async Task<IResult> LogoutAsync(SignInManager<IdentityUser> signIn)
    {
        await signIn.SignOutAsync();
        return TypedResults.NoContent();
    }

    /// <summary>Who the caller is, for the line in the client's header.</summary>
    private static IResult Me(ICurrentUser currentUser, HttpContext context) =>
        TypedResults.Ok(new MeResponse(currentUser.OwnerId, context.User.Identity?.Name));

    private static MeResponse Describe(IdentityUser user) => new(user.Id, user.UserName);

    // The same RFC 9457 shape ValidationFilter<T> answers with, so the client has
    // one branch for "the server rejected this" rather than two. AddProblemDetails
    // in Program.cs is what gives it a body.
    //
    // CamelCase, and it has to be, because it is what the other half of this
    // contract already does. ValidationFilter<T> runs every member name through
    // exactly this call, and the client matches the key against its own field names
    // to decide where to show the message. Written as `nameof(request.Password)`
    // and left alone, this answered "Password", the form found no field called
    // that, and the sentence appeared in the banner at the top instead of under the
    // password box -- correct, visible, and in the wrong place. Found by sending
    // the request rather than by reading the code, which is the only way this one
    // shows up.
    private static IResult Refused(string message, string field) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>
            {
                [JsonNamingPolicy.CamelCase.ConvertName(field)] = [message],
            },
            title: "The request was refused.");
}

/// <summary>Who is signed in. The id is what rows are owned by.</summary>
public sealed record MeResponse(string? OwnerId, string? Name);

/// <summary>A new account, and the code that permits it.</summary>
public sealed class RegisterRequest
{
    // The bounds are the server's, and the client validates shape rather than
    // limits -- the rule #6 settled, so no number is written down in two languages.
    // Identity checks the password's own length itself and says so in its message;
    // the ceiling here exists because a megabyte-long password is a request to hash
    // a megabyte.
    [Required]
    [StringLength(64, MinimumLength = 3)]
    public required string UserName { get; init; }

    [Required]
    [StringLength(128)]
    public required string Password { get; init; }

    // Not [Required]: in Development with no code configured there is nothing to
    // send, and a required field would make the local loop demand a value that
    // means nothing. RegistrationPolicy decides whether an absent code is
    // acceptable.
    [StringLength(128)]
    public string? InviteCode { get; init; }
}

/// <summary>An existing account.</summary>
public sealed class LoginRequest
{
    [Required]
    [StringLength(64)]
    public required string UserName { get; init; }

    // No MinimumLength, deliberately, and it is not an oversight to be tidied. The
    // password rules belong to registration; applying them here would refuse a
    // short password that an older account legitimately has, and it would tell an
    // attacker the minimum length before they had guessed anything.
    [Required]
    [StringLength(128)]
    public required string Password { get; init; }
}
