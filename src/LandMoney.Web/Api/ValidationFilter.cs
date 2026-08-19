using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace LandMoney.Web.Api;

/// <summary>
/// Runs the DataAnnotations attributes on the <typeparamref name="T"/> argument of an
/// endpoint and answers 400 with a ValidationProblem if any of them fail.
/// </summary>
// This is the minimal-API counterpart to an MVC action filter, and the reason a
// controller was not needed: [ApiController] does exactly this and no more.
// Written by hand rather than taken from .NET 10's AddValidation(), which is
// marked [Experimental] and will not compile without suppressing ASP0029.
//
// Generic over T so one class serves every endpoint that takes a body. The type
// argument is what tells the filter which of the handler's parameters to look
// at -- AppDbContext and CancellationToken are arguments too.
public sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        // Arguments holds every parameter the handler is about to receive, in
        // order: the bound body, but also the AppDbContext and the
        // CancellationToken that the DI container filled in. OfType<T> is what
        // picks out the one this filter was closed over.
        //
        // No argument of type T means the body never bound. Nothing to validate,
        // and nothing useful to say about it -- hand it on and let the model
        // binder's own 400 stand rather than inventing a second explanation.
        if (context.Arguments.OfType<T>().FirstOrDefault() is not { } argument)
        {
            return await next(context);
        }

        var results = new List<ValidationResult>();

        // validateAllProperties: true is the whole game. The parameter defaults
        // to false, and false runs [Required] and nothing else -- every [Range],
        // [RegularExpression] and [NotFarInFuture] is skipped without a word. The
        // endpoint then accepts a negative amount while the attributes sit there
        // looking correct, which is why this is the most common way the API is
        // misused.
        //
        // TryValidateObject also does not descend into nested objects. Flat
        // record, so it does not matter today; it stops not mattering the day
        // this type holds a child.
        var isValid = Validator.TryValidateObject(
            argument,
            new ValidationContext(argument),
            results,
            validateAllProperties: true);

        if (isValid)
        {
            return await next(context);
        }

        var errors = results
            // MemberNames can name several fields at once, and can be empty when
            // a rule is about the object as a whole. DefaultIfEmpty keeps that
            // second case from vanishing: SelectMany over an empty list drops the
            // result silently, and the request would then be rejected with a 400
            // that lists no reason at all.
            .SelectMany(result => result.MemberNames
                .DefaultIfEmpty(string.Empty)
                .Select(memberName => (
                    Field: JsonNamingPolicy.CamelCase.ConvertName(memberName),
                    Message: result.ErrorMessage ?? "Invalid value.")))
            .GroupBy(error => error.Field)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).ToArray());

        // Returning a result instead of calling next() is what makes this a short
        // circuit: the handler never runs, so CreateAsync can assume its request
        // is already sound and needs no defensive checks of its own.
        return TypedResults.ValidationProblem(errors);
    }
}
