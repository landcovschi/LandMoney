using LandMoney.Web.Data;
using LandMoney.Web.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LandMoney.Web.Api;

/// <summary>The two endpoints of #3: create one transaction, list them newest first.</summary>
// Minimal APIs rather than a controller, chosen in #3. Both are current in
// .NET 10 and either would be defensible; what decided it is that [ApiController]
// is already familiar territory, and this repository's rule is that skill gained
// outweighs comfort. The one real advantage controllers used to hold -- an
// automatic 400 ProblemDetails from DataAnnotations -- is answered here by
// ValidationFilter<T>, about fifteen lines. (.NET 10 ships that as
// AddValidation(), which was the first plan until the build refused it: the API
// is [Experimental] and needs ASP0029 suppressed.) What is given up is the
// structure a controller imposes for
// free; this class buys it back by hand, which is why the handlers are named
// static methods rather than lambdas: a lambda cannot be found by its name, and
// nothing about a group of two endpoints stays true at twenty.
public static class TransactionEndpoints
{
    /// <summary>Registers /api/transactions. Called from Program.cs.</summary>
    // An extension method on IEndpointRouteBuilder is the minimal-API equivalent
    // of the file a controller lives in: it keeps Program.cs to one line per
    // feature instead of growing every route into the startup path.
    public static RouteGroupBuilder MapTransactionEndpoints(this IEndpointRouteBuilder routes)
    {
        // MapGroup so the prefix is written once. Filters, authorization and
        // OpenAPI metadata added to the group later apply to everything in it --
        // that is the extension point, and the reason the group is returned
        // rather than void.
        var group = routes.MapGroup("/api/transactions");

        // The filter is attached to the POST only. GET takes no body, so there
        // is nothing to validate, and hanging it on the group would run a filter
        // that never finds its argument on every list request.
        group.MapPost("/", CreateAsync)
            .AddEndpointFilter<ValidationFilter<CreateTransactionRequest>>();

        group.MapGet("/", ListAsync);

        return group;
    }

    // The return type is written out rather than left as Task<IResult> on
    // purpose: a concrete Created<T> is what tells OpenAPI, and later the
    // TypeScript generator, which status codes and which body shape this
    // endpoint produces. IResult erases all of it.
    //
    // The 400 does not appear in the signature because it is not produced here:
    // ValidationFilter<CreateTransactionRequest> sits in front of this method and
    // answers a failing request before the handler is entered. The cost of that
    // separation is that the signature no longer tells the whole truth about the
    // status codes this route can return, which is a thing to remember when
    // OpenAPI is turned on and the 400 has to be declared with ProducesProblem.
    private static async Task<Created<TransactionResponse>> CreateAsync(
        CreateTransactionRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var transaction = new Transaction
        {
            // Id is deliberately not set. The model snapshot shows the key as
            // ValueGeneratedOnAdd, which EF Core applies to a Guid key by
            // convention: it fills the value itself when the property is still
            // the CLR default, before the INSERT is sent, so the id is available
            // here without a round trip -- which was the point of choosing a Guid
            // in the first place. Assigning Guid.NewGuid() by hand would work and
            // would be redundant, and it would be strictly worse: EF's generator
            // produces sequential values, while NewGuid produces the random v4
            // whose index-scatter cost is written down on the entity.
            OccurredAt = request.OccurredAt,
            Amount = request.Amount,

            // ToUpperInvariant, not ToUpper. ToUpper follows the machine's
            // culture, and Turkish maps a dotted lower-case i to a dotted capital
            // one, so "try" uppercases to a string that is not "TRY" on a Turkish
            // machine. Two spellings of one currency in a GROUP BY is a bug that
            // appears only on someone else's computer.
            Currency = request.Currency.ToUpperInvariant(),

            Description = request.Description,

            // Category is left null on purpose: "not categorised yet" is a real
            // state until the model in slice 4 fills it, and the request type
            // does not offer the field at all, so a client cannot pre-empt it.
            // CreatedAt is left to the entity's initializer.
        };

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(cancellationToken);

        // 201 with no Location header, deliberately. Location is optional in
        // RFC 9110, and #3 stops at two endpoints, so there is no GET-by-id for
        // it to point at -- a URL that 404s would be worse than an absent header.
        // The cast picks the string? overload; without it the call is ambiguous.
        return TypedResults.Created((string?)null, ToResponse(transaction));
    }

    private static async Task<Ok<IReadOnlyList<TransactionResponse>>> ListAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var transactions = await db.Transactions
            // Two sort keys, and the second is not decoration. OccurredAt became
            // a DateOnly in #17, so an ordinary week has several rows sharing a
            // day; with one key their order within that day is whatever Postgres
            // finds cheapest, which looks stable in testing and reshuffles after
            // the table is next rewritten. CreatedAt is the tiebreak precisely
            // because it kept full precision when OccurredAt gave it up.
            .OrderByDescending(t => t.OccurredAt)
            .ThenByDescending(t => t.CreatedAt)

            // Projecting before ToListAsync is what puts these columns in the
            // SELECT list. Materialising entities first and mapping afterwards
            // reads almost the same and fetches every column of every row.
            //
            // Note there is no AsNoTracking() here, and it is not an oversight:
            // EF only tracks entities, and this query returns TransactionResponse,
            // which is not one. The call would be a no-op. It would be required
            // the moment this returned Transaction instead.
            .Select(t => new TransactionResponse(
                t.Id,
                t.OccurredAt,
                t.Amount,
                t.Currency,
                t.Description,
                t.Category,
                t.CreatedAt))
            .ToListAsync(cancellationToken);

        // The explicit type argument is needed because List<T> is being widened
        // to IReadOnlyList<T>, and inference would otherwise pin the return type
        // to Ok<List<TransactionResponse>> and fail to match the signature.
        return TypedResults.Ok<IReadOnlyList<TransactionResponse>>(transactions);

        // No paging and no limit, matching #3. Worth saying out loud rather than
        // forgetting: this returns the whole table, which is right for one
        // person's weekly spending and wrong the moment it is not.
    }

    // Kept separate from the projection in ListAsync even though the two build
    // the same record, because they cannot be shared: EF has to translate the
    // projection into SQL and cannot translate a call to this method. Merging
    // them means an Expression<Func<,>> field and Compile() on the write path --
    // more machinery than seven arguments are worth at this size. If a field is
    // ever added, both places have to change; the compiler will say so, because
    // the record's constructor is positional.
    private static TransactionResponse ToResponse(Transaction transaction) => new(
        transaction.Id,
        transaction.OccurredAt,
        transaction.Amount,
        transaction.Currency,
        transaction.Description,
        transaction.Category,
        transaction.CreatedAt);
}
