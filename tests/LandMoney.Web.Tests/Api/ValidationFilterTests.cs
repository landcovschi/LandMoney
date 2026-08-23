using System.ComponentModel.DataAnnotations;
using LandMoney.Web.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace LandMoney.Web.Tests.Api;

/// <summary>The filter that turns a failing DataAnnotations run into a 400.</summary>
// No WebApplicationFactory and no Microsoft.AspNetCore.Mvc.Testing, which #21 asked
// to have discussed before adding: an IEndpointFilter is an ordinary object with one
// method, and EndpointFilterInvocationContext.Create builds its argument. What that
// leaves untested is the wiring -- that the filter is attached to the POST and not
// to the group, and that a 400 leaving the process carries the RFC 9457 body
// AddProblemDetails writes. Both need a real request, and both belong with the
// endpoints rather than here.
//
// The clock is pinned for the same reason it is pinned next door:
// CreateTransactionRequest carries [PlausibleDate], so an unpinned test of "a valid
// request" would begin failing five years after it was written.
public class ValidationFilterTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Fact]
    public async Task A_sound_request_reaches_the_handler()
    {
        var (result, reachedHandler) = await Run(Valid());

        Assert.True(reachedHandler);
        Assert.IsType<Ok<string>>(result);
    }

    // Returning a result instead of calling next is what lets CreateAsync assume
    // its request is already sound. If the handler still ran, every defensive
    // check the endpoint deliberately does not have would become a way to write a
    // bad row.
    [Fact]
    public async Task A_failing_request_never_reaches_the_handler()
    {
        var (result, reachedHandler) = await Run(Valid() with { Currency = "EU1" });

        Assert.False(reachedHandler);
        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    // The key the React form looks the message up by. The C# property is
    // OccurredAt and the JSON field is occurredAt; the filter converts, and
    // without the conversion every message lands under a key no client asks for.
    [Fact]
    public async Task Error_keys_are_camelCase_to_match_the_JSON_the_client_sent()
    {
        var (result, _) = await Run(Valid() with { OccurredAt = Today.AddYears(-10) });

        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Contains("occurredAt", problem.ProblemDetails.Errors.Keys);
        Assert.DoesNotContain("OccurredAt", problem.ProblemDetails.Errors.Keys);
    }

    // This is the test that fails if validateAllProperties: true is ever dropped.
    // The parameter defaults to false, false runs [Required] and nothing else, and
    // every [Range], [RegularExpression], [DecimalScale] and [PlausibleDate] is
    // then skipped in silence. The request below is complete, so [Required] has no
    // objection to it, and the endpoint would store minus five euros.
    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task Rules_other_than_Required_are_run(int amount)
    {
        var (result, reachedHandler) = await Run(Valid() with { Amount = amount });

        Assert.False(reachedHandler);
        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Contains("amount", problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public async Task A_third_decimal_place_is_refused_through_the_filter_too()
    {
        var (result, _) = await Run(Valid() with { Amount = 12.345m });

        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Contains("amount", problem.ProblemDetails.Errors.Keys);
    }

    // MemberNames is empty when a rule is about the object rather than a field.
    // SelectMany over an empty list drops the result entirely, so without the
    // DefaultIfEmpty in the filter this request comes back as a 400 listing no
    // reason at all: a rejection nothing can show anybody.
    [Fact]
    public async Task A_rule_with_no_member_name_still_produces_an_entry()
    {
        var (result, reachedHandler) = await Run(new ObjectLevelRule());

        Assert.False(reachedHandler);
        var problem = Assert.IsType<ValidationProblem>(result);
        var entry = Assert.Single(problem.ProblemDetails.Errors);
        Assert.Equal(string.Empty, entry.Key);
        Assert.Equal(["The request as a whole is wrong."], entry.Value);
    }

    // Two rules on one field are two messages under one key, not two keys, which
    // is what the GroupBy in the filter is for. Amount carries [Range] and
    // [DecimalScale(2)], and -0.005 fails both: below the floor, and a third
    // decimal place.
    [Fact]
    public async Task Several_messages_about_one_field_arrive_under_one_key()
    {
        var (result, _) = await Run(Valid() with { Amount = -0.005m });

        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Equal(2, problem.ProblemDetails.Errors["amount"].Length);
    }

    // Found by writing the test above with Description = "" and getting one
    // message where two were expected. Validator tests the RequiredAttribute on a
    // property first and returns at once if it fails, so the other rules on that
    // property never run. Description carries [Required] and
    // [StringLength(MinimumLength = 1)] and an empty string breaks both, yet only
    // the first is reported.
    //
    // It is the behaviour anyone would want -- "this is required" and "this is too
    // short" about the same empty box is noise -- but it is worth pinning, because
    // it means the count of messages under a key is not the count of broken rules,
    // and a form that lists every reason at once will still show one for an empty
    // field.
    [Fact]
    public async Task A_failing_Required_suppresses_the_other_rules_on_that_field()
    {
        var (result, _) = await Run(Valid() with { Description = "" });

        var problem = Assert.IsType<ValidationProblem>(result);
        var message = Assert.Single(problem.ProblemDetails.Errors["description"]);
        Assert.Contains("required", message, StringComparison.OrdinalIgnoreCase);
    }

    // The filter is generic so that one class serves every endpoint with a body,
    // and Arguments holds the AppDbContext and the CancellationToken as well. No
    // argument of type T means the body never bound: the model binder has already
    // written its own 400, and inventing a second explanation would replace a
    // precise message with a vague one.
    [Fact]
    public async Task An_invocation_with_no_argument_of_that_type_is_left_alone()
    {
        var context = EndpointFilterInvocationContext.Create(
            HttpContextWithClockAt(Today), CancellationToken.None);
        var reachedHandler = false;

        var result = await new ValidationFilter<CreateTransactionRequest>().InvokeAsync(
            context,
            _ =>
            {
                reachedHandler = true;
                return ValueTask.FromResult<object?>(TypedResults.Ok("handled"));
            });

        Assert.True(reachedHandler);
        Assert.IsType<Ok<string>>(result);
    }

    // The reason the ValidationContext is built with HttpContext.RequestServices
    // rather than with the one-argument constructor. The clock here says 1 January
    // 2000, so the 2nd is tomorrow and inside the one day of slack the request
    // allows. Against the real system clock that date is a quarter of a century
    // back and far outside the five-year bound, so acceptance can only mean the
    // registered clock was consulted -- and 2000-01-02 can never drift into being
    // valid for some other reason.
    [Fact]
    public async Task A_service_registered_on_the_request_reaches_the_attributes()
    {
        var millennium = new DateOnly(2000, 1, 1);
        var request = Valid() with { OccurredAt = millennium.AddDays(1) };

        var (result, reachedHandler) = await Run(request, now: millennium);

        Assert.True(reachedHandler);
        Assert.IsType<Ok<string>>(result);
    }

    // The same request with no clock registered, so the test above is known to be
    // about the clock rather than about that date being harmless. This is also the
    // one place the production fallback is exercised through the filter.
    [Fact]
    public async Task That_same_date_is_refused_when_no_clock_is_registered()
    {
        var request = Valid() with { OccurredAt = new DateOnly(2000, 1, 2) };
        var context = EndpointFilterInvocationContext.Create(new DefaultHttpContext(), request);

        var result = await new ValidationFilter<CreateTransactionRequest>().InvokeAsync(
            context,
            _ => ValueTask.FromResult<object?>(TypedResults.Ok("handled")));

        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Contains("occurredAt", problem.ProblemDetails.Errors.Keys);
    }

    private static async Task<(object? Result, bool ReachedHandler)> Run<T>(
        T argument, DateOnly? now = null)
        where T : class
    {
        var context = EndpointFilterInvocationContext.Create(
            HttpContextWithClockAt(now ?? Today), argument);
        var reachedHandler = false;

        var result = await new ValidationFilter<T>().InvokeAsync(
            context,
            _ =>
            {
                reachedHandler = true;
                return ValueTask.FromResult<object?>(TypedResults.Ok("handled"));
            });

        return (result, reachedHandler);
    }

    // RequestServices is what the filter hands to the ValidationContext, and
    // therefore the only place an attribute can find anything.
    private static DefaultHttpContext HttpContextWithClockAt(DateOnly day) =>
        new()
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<TimeProvider>(FixedTimeProvider.At(day))
                .BuildServiceProvider(),
        };

    private static CreateTransactionRequest Valid() => new()
    {
        OccurredAt = Today,
        Amount = 12.34m,
        Currency = "EUR",
        Description = "Groceries",
    };

    /// <summary>A type whose only rule is about the object, so it reports no member name.</summary>
    // No properties, on purpose. Validator.TryValidateObject returns at the first
    // property failure and never reaches IValidatableObject, so anything invalid
    // here would hide the case this type exists to produce.
    private sealed class ObjectLevelRule : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            yield return new ValidationResult("The request as a whole is wrong.");
        }
    }
}
