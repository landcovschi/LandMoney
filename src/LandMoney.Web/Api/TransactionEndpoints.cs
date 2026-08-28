using System.ComponentModel.DataAnnotations;
using LandMoney.Web.Categorizing;
using LandMoney.Web.Data;
using LandMoney.Web.Import;
using LandMoney.Web.Models;
using Microsoft.AspNetCore.Http.Features;
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

        // #62. No ValidationFilter: there is no JSON body for it to find, and the
        // rules it would run are run per row inside the handler instead -- on the
        // same CreateTransactionRequest, through the same Validator call, so the two
        // ways into this table cannot drift apart.
        group.MapPost("/import", ImportAsync);

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
        CategorizerClient categorizer,
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

            // Category is still not set here, and the request type still does not
            // offer the field, so a client cannot pre-empt it. It is filled in
            // below instead of at the initializer because the value comes from
            // another process and may not arrive.
            // CreatedAt is left to the entity's initializer.
        };

        // #39. The category is a guess about the user's data and the transaction
        // is the user's data, so this line may not be allowed to cost the row:
        // CategorizerClient turns every failure -- unreachable, slow, a body it
        // cannot read -- into null, and null is the state Category has been
        // designed for since #1. Nothing here needs a try/catch, and adding one
        // would only catch the exceptions that class deliberately lets past,
        // which is the caller's own cancellation.
        //
        // Before SaveChangesAsync rather than after, so the row is written once.
        // What lost: saving first and updating the row when the answer comes
        // back, which survives this process dying mid-call and costs two writes,
        // a second code path and a window in which the API has answered 201 with
        // a category the database does not have yet. The failure it protects
        // against is one where the user's transaction is lost, and the tight
        // timeout in Program.cs already bounds that window to two seconds.
        //
        // transaction.Currency, not request.Currency: the handler uppercases it
        // above, and the categorizer should be shown what is about to be stored
        // rather than what was typed.
        //
        // #59: the category and the name of what produced it are set together, from
        // one nullable value, so the two columns cannot disagree. Writing them from
        // two separate calls -- or defaulting the source to "rules" here -- is what
        // the single record exists to prevent: a row saying `model` because
        // configuration said so rather than because a model answered.
        var suggestion = await categorizer.SuggestCategoryAsync(
            transaction.Description, transaction.Amount, transaction.Currency, cancellationToken);

        transaction.Category = suggestion?.Category;
        transaction.CategorySource = suggestion?.Source;

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

    /// <summary>The only content type this endpoint accepts, and it is a security control.</summary>
    // **Load-bearing, not cosmetic, and the reason the file does not arrive as
    // multipart/form-data.** AuthenticationSetup.cs records two CSRF locks: the
    // SameSite=Lax cookie, and the JSON content type a cross-site form cannot set
    // without a preflight this server never answers. multipart/form-data is a
    // *form-submittable* type, so a multipart endpoint would keep only the first of
    // those; text/csv is not, so both survive. Relaxing this check to accept
    // anything -- which looks like tolerance of a client that sends
    // application/octet-stream -- removes a control while reading like tidying up.
    //
    // Choosing it also avoids the other half of the multipart cost: minimal APIs
    // apply antiforgery validation to any endpoint that binds a form, so IFormFile
    // would need .DisableAntiforgery() and app.UseAntiforgery(), machinery this
    // application deliberately has none of.
    private const string CsvContentType = "text/csv";

    /// <summary>How large an uploaded file may be.</summary>
    // A megabyte is roughly 15,000 rows of this shape, comfortably more than the
    // row cap below, so whichever limit a real file meets first it meets the one
    // with the clearer message.
    private const int MaxImportBytes = 1024 * 1024;

    /// <summary>How many rows one import may carry.</summary>
    // The endpoint holds every row in memory, queries once over their whole date
    // range and inserts them in one transaction. All three are fine at this size
    // and none of them is fine unbounded.
    private const int MaxImportRows = 5000;

    /// <summary>How many per-row explanations are sent back.</summary>
    // A file where every row is wrong would otherwise answer with 5,000 sentences.
    // The counts in ImportResponse are exact whatever this does, so what is lost by
    // truncating is the detail and never the summary -- and the response says it
    // truncated rather than leaving the reader to notice.
    private const int MaxReportedProblems = 200;

    // Deliberately does not take CategorizerClient, and that absence is a decision
    // rather than an omission. #39 categorises on the create path *before*
    // SaveChangesAsync, one HTTP call per transaction; a 300-row file would be 300
    // calls, and #59's own measurement of the broken case -- 2.15 s per save against
    // a categorizer that is not there -- makes that a request that can legitimately
    // run for ten minutes. What lost: a batch endpoint on the Python service, which
    // is the honest fix and is a change to a service #61 has only just deployed.
    //
    // So every imported row arrives with Category and CategorySource null, which is
    // the state Category has been designed for since #1, and the response reports
    // how many. The cost is real and is the shape #39 keeps paying for: a dependency
    // the application is designed to run without is a dependency whose absence
    // nothing reports. Here it is reported, and the backfill is its own issue.
    private static async Task<Results<Ok<ImportResponse>, ProblemHttpResult>> ImportAsync(
        HttpContext http,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // Split rather than string-compared whole, because a browser sends
        // "text/csv" and a File object with a charset sends "text/csv; charset=utf-8".
        var mediaType = http.Request.ContentType?.Split(';', 2)[0].Trim();

        if (!string.Equals(mediaType, CsvContentType, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                detail: $"Send the file as the request body with Content-Type: {CsvContentType}.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // Before a single byte is read, which is the only time this is writable --
        // the feature reports IsReadOnly once the body has started arriving. Setting
        // it lets Kestrel refuse an oversized upload itself, draining the connection
        // properly, rather than this handler abandoning a body the client is still
        // sending.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maxBodySize)
        {
            maxBodySize.MaxRequestBodySize = MaxImportBytes;
        }

        byte[]? bytes;

        try
        {
            bytes = await ReadCappedAsync(http.Request.Body, MaxImportBytes, cancellationToken);
        }
        // What Kestrel throws when the limit above is exceeded. Caught so the answer
        // is this endpoint's own sentence naming the limit, rather than whatever
        // UseExceptionHandler makes of an exception escaping a handler.
        catch (BadHttpRequestException)
        {
            bytes = null;
        }

        if (bytes is null)
        {
            return TypedResults.Problem(
                detail: $"The file is larger than the {MaxImportBytes / 1024} KB this endpoint accepts.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (!CsvText.TryDecode(bytes, out var text, out var encodingProblem))
        {
            return TypedResults.Problem(
                detail: encodingProblem, statusCode: StatusCodes.Status400BadRequest);
        }

        ParsedFile file;

        try
        {
            file = TransactionCsv.Parse(text);
        }
        // A header nothing can be done with, or a quote that is never closed. The
        // only failures that refuse a whole file: everything else is a row problem
        // and is reported beside the rows that did import.
        catch (CsvFormatException exception)
        {
            return TypedResults.Problem(
                detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Rows.Count > MaxImportRows)
        {
            return TypedResults.Problem(
                detail: $"The file holds {file.Rows.Count} rows; this endpoint takes {MaxImportRows} at a time.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var problems = new List<ImportRowProblem>();
        var accepted = new List<(int LineNumber, CreateTransactionRequest Request)>();

        foreach (var row in file.Rows)
        {
            if (row.Request is not { } request)
            {
                // Reason is non-null whenever Request is null -- the invariant
                // ImportRow's two factory methods exist to hold.
                problems.Add(new ImportRowProblem(
                    row.LineNumber, ImportOutcomes.Rejected, row.Reason ?? "Unreadable."));
                continue;
            }

            var results = new List<ValidationResult>();

            // The same two arguments ValidationFilter<T> passes, for the same two
            // reasons, and both fail silently if dropped. validateAllProperties:
            // true or every rule but [Required] is skipped, so a negative amount
            // imports. RequestServices or PlausibleDateAttribute never finds its
            // TimeProvider and quietly takes the fallback clock.
            var isValid = Validator.TryValidateObject(
                request,
                new ValidationContext(request, http.RequestServices, items: null),
                results,
                validateAllProperties: true);

            if (!isValid)
            {
                problems.Add(new ImportRowProblem(
                    row.LineNumber,
                    ImportOutcomes.Rejected,
                    string.Join(" ", results.Select(result => result.ErrorMessage ?? "Invalid value."))));
                continue;
            }

            accepted.Add((row.LineNumber, request));
        }

        // One query for the whole file rather than one per row. Bounded by the
        // file's own date range, and the owner filter is applied by AppDbContext
        // without this asking -- so a row belonging to somebody else can neither be
        // seen here nor counted as a duplicate of one of these.
        // ix_transactions_owner_id_occurred_at_created_at covers the predicate.
        var existing = new HashSet<TransactionKey>();

        if (accepted.Count > 0)
        {
            var earliest = accepted.Min(row => row.Request.OccurredAt);
            var latest = accepted.Max(row => row.Request.OccurredAt);

            var stored = await db.Transactions
                .Where(transaction => transaction.OccurredAt >= earliest && transaction.OccurredAt <= latest)
                .Select(transaction => new
                {
                    transaction.OccurredAt,
                    transaction.Amount,
                    transaction.Currency,
                    transaction.Description,
                })
                .ToListAsync(cancellationToken);

            foreach (var transaction in stored)
            {
                existing.Add(new TransactionKey(
                    transaction.OccurredAt,
                    transaction.Amount,
                    transaction.Currency,
                    transaction.Description));
            }
        }

        var seen = new HashSet<TransactionKey>();
        var toAdd = new List<Transaction>();

        foreach (var (lineNumber, request) in accepted)
        {
            // ToUpperInvariant here rather than in TransactionCsv, so this is the
            // same line CreateAsync writes for the same reason -- ToUpper follows
            // the machine's culture and a Turkish machine produces a string that is
            // not "TRY". Done before the key is built, or "mdl" and "MDL" would be
            // two different transactions.
            var currency = request.Currency.ToUpperInvariant();
            var key = new TransactionKey(request.OccurredAt, request.Amount, currency, request.Description);

            if (existing.Contains(key))
            {
                problems.Add(new ImportRowProblem(
                    lineNumber,
                    ImportOutcomes.Skipped,
                    "An identical transaction is already recorded. If this is a genuine second purchase, "
                    + "add it from the form."));
                continue;
            }

            if (!seen.Add(key))
            {
                problems.Add(new ImportRowProblem(
                    lineNumber, ImportOutcomes.Skipped, "An identical row appears earlier in this file."));
                continue;
            }

            toAdd.Add(new Transaction
            {
                // Id, CreatedAt and OwnerId are all left alone, exactly as in
                // CreateAsync: EF fills the key, the entity's initializer fills the
                // timestamp, and AppDbContext.SaveChangesAsync stamps the owner on
                // every added entity -- which is why a bulk add needs no per-row
                // ownership code and cannot forget it.
                //
                // Category and CategorySource stay null. See the note on this
                // method: the import does not call the categorizer.
                OccurredAt = request.OccurredAt,
                Amount = request.Amount,
                Currency = currency,
                Description = request.Description,
            });
        }

        // Guarded rather than called unconditionally. EF would answer 0 without
        // opening a connection anyway, but the guard says so out loud, and it makes
        // a file whose rows were all rejected a request that provably never touches
        // Postgres.
        if (toAdd.Count > 0)
        {
            db.Transactions.AddRange(toAdd);
            await db.SaveChangesAsync(cancellationToken);
        }

        // One transaction for the whole file, which is what a single SaveChanges
        // gives: an insert that fails takes the others with it rather than leaving
        // a half-imported file nobody can tell from a fully imported one. Every
        // row here has already passed the same validation the single-row POST
        // applies, so the realistic remaining failure is the database being
        // unreachable -- which should indeed take the whole thing.
        var ordered = problems.OrderBy(problem => problem.LineNumber).ToList();
        var truncated = ordered.Count > MaxReportedProblems;

        return TypedResults.Ok(new ImportResponse(
            Rows: file.Rows.Count,
            Imported: toAdd.Count,
            Skipped: ordered.Count(problem => problem.Outcome == ImportOutcomes.Skipped),
            Rejected: ordered.Count(problem => problem.Outcome == ImportOutcomes.Rejected),
            IgnoredColumns: file.IgnoredColumns,
            ProblemsTruncated: truncated,
            Problems: truncated ? ordered.Take(MaxReportedProblems).ToList() : ordered));
    }

    /// <summary>The whole body, or null if it is longer than <paramref name="max"/>.</summary>
    // Reads rather than CopyToAsync into a MemoryStream, because that has no cap and
    // the cap is the point. Null rather than an exception for "too long": it is an
    // expected answer about the request, not an exceptional event, and the caller
    // has a status for it.
    private static async Task<byte[]?> ReadCappedAsync(Stream body, int max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
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
