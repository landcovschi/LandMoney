using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using LandMoney.Web.Categorizing;
using LandMoney.Web.Data;
using LandMoney.Web.Export;
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

        // #95. Still no filter: `limit` and `cursor` are query parameters, not a
        // body, so there is nothing for ValidationFilter<T> to find -- and both are
        // answered inside the handler rather than by attributes, because their two
        // wrong values want two different answers. A limit out of range is clamped
        // and a cursor that does not parse is a 400, and the argument for the
        // asymmetry is on ClampPageSize.
        group.MapGet("/", ListAsync);

        // #95. A literal segment, registered before the {id:guid} routes for the
        // reason /labelled and /category-suggestion are: routing would not confuse
        // them in any case -- a constrained parameter does not match this word -- and
        // the file reads specific-then-parameterised.
        //
        // A month is a query parameter rather than a path segment (/summary/2026-08),
        // and the difference is what a wrong one means. A path names a resource, so a
        // bad month there is a 404 about a thing that does not exist; here it is a
        // 400 about a question that cannot be answered, which is what it is. It also
        // leaves room for the parameter this will grow first -- a range rather than a
        // month -- without inventing a second URL.
        group.MapGet("/summary", MonthSummaryAsync);

        // #62. No ValidationFilter: there is no JSON body for it to find, and the
        // rules it would run are run per row inside the handler instead -- on the
        // same CreateTransactionRequest, through the same Validator call, so the two
        // ways into this table cannot drift apart.
        group.MapPost("/import", ImportAsync);

        // #63. PATCH rather than PUT, because this replaces one field and not the
        // row: PUT means "here is the resource", and a PUT that ignores four of the
        // fields it was sent is a lie about what happened to them.
        //
        // The {id:guid} constraint is what keeps this from swallowing /import --
        // routing scores a literal segment above a parameter, and a constrained
        // parameter does not match "import" in the first place. It also turns a
        // malformed id into a 404 from routing rather than a 400 from the binder,
        // which is the right answer: an id that cannot exist and an id that does not
        // exist are the same fact to a caller.
        group.MapPatch("/{id:guid}", UpdateCategoryAsync)
            .AddEndpointFilter<ValidationFilter<UpdateCategoryRequest>>();

        // #94. PUT rather than a second PATCH, and the two live side by side on one
        // route because they answer two different questions about the same row.
        //
        // A second PATCH is not available in any case -- one method plus one route
        // is one endpoint -- but the interesting half is that it would be the wrong
        // shape even if it were. PATCH means "change these fields and leave the rest
        // as they are", and that is precisely the reading #63 refused to let an
        // amount travel under: a body that may carry an amount and may omit it is a
        // body a stale screen can use to overwrite money it was not editing. PUT
        // means "this is what it is now", so every field it writes is a field the
        // sender had to state -- and the four it does not carry (id, createdAt,
        // category, categorySource) are the server's, which no method makes
        // writable.
        //
        // The strict reading of PUT would have an omitted field cleared, and by that
        // reading this is not quite one: `category` survives a PUT that does not
        // mention it. The honest description is that the resource this replaces is
        // *the four fields a person typed*, which is the whole of what a client owns
        // here. A sub-resource -- PUT /{id}/details -- would say that in the URL and
        // buys a path segment nobody would ever fetch on its own.
        group.MapPut("/{id:guid}", UpdateAsync)
            .AddEndpointFilter<ValidationFilter<UpdateTransactionRequest>>();

        // #94. No filter and no body: the id is in the route, and there is nothing
        // else a delete could get wrong.
        group.MapDelete("/{id:guid}", DeleteAsync);

        // #67. A POST that writes nothing, which is the one thing about it worth
        // arguing over. A GET reads better for a question -- it is idempotent, it
        // is cacheable, and "does not write anything" is exactly what the method
        // means -- and it lost twice over. The description would travel in a query
        // string, so one person's spending would be written into every access log
        // between here and the process, which is the rule #64 keeps for log lines
        // and metric tags arriving at a URL. And a GET is a top-level navigation, so
        // the SameSite=Lax cookie is sent with it: AuthenticationSetup.cs records
        // two CSRF locks and a JSON POST keeps both, where a GET keeps neither.
        //
        // The literal segment cannot be shadowed by the PATCH above -- different
        // method, and {id:guid} would not match this word in any case -- but it is
        // registered after it deliberately, so the file reads in the order #20
        // established: the specific routes, then the parameterised one.
        group.MapPost("/category-suggestion", SuggestCategoryAsync)
            .AddEndpointFilter<ValidationFilter<CategorySuggestionRequest>>();

        // #89. A GET, and the one place the argument for #67's POST does not carry
        // over: nothing about this request is a value, so there is no description to
        // write into an access log, and it is a read in the sense the method means --
        // idempotent, and safe to repeat. What it keeps from that decision is the
        // literal segment, registered after the {id:guid} PATCH for the same reason
        // /category-suggestion is: routing would not confuse them in any case, and the
        // file reads specific-then-parameterised.
        //
        // No ValidationFilter, and no query string to validate. Every choice this
        // endpoint could have offered -- a date range, a category, a limit -- is one
        // more thing that can be got wrong in a file whose whole job is to be appended
        // to another file, and `head` and `grep` already exist for the day one is
        // wanted.
        group.MapGet("/labelled", ExportLabelledAsync);

        // #93. A POST, and here the argument is the plain one rather than #67's: this
        // writes. It marks rows as owing a category, which is a change to the
        // database and a decision to spend money -- every row it marks is a model
        // call the sweep will make.
        //
        // No ValidationFilter and no body at all. Every option this could have taken
        // -- a date range, a category, a limit -- is a way to disagree with the count
        // the screen showed before the button was pressed, and that count is the only
        // thing standing between a person and a bill they did not expect. What is
        // marked is exactly what PendingCategorization.Backfillable selects, and the
        // client counts the same rows out of the list it already has.
        group.MapPost("/backfill-categories", BackfillCategoriesAsync);

        // #95. The same path, the other verb, and that is the argument for it: this
        // answers "how many rows would that POST act on", so the two are one
        // collection asked about in the two ways HTTP has -- count it, or do it. A
        // URL of its own would be a second name for one predicate, and these two
        // drifting apart is silent and is money.
        //
        // It exists because paging took the count away from the client: #93 counted
        // the loaded rows, and a loaded page is no longer the table.
        group.MapGet("/backfill-categories", CountBackfillableAsync);

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

            // Category is still not set here, and the request type still does not
            // offer the field, so a client cannot pre-empt it. As of #92 it is not
            // set anywhere in this method: the row is written without one and
            // CategorizerSweep fills it in afterwards.
            // CreatedAt is left to the entity's initializer.

            // #92. The one line that replaces the call that used to be here. It
            // says "a category is owed for this row", which is what the sweep
            // selects on -- see PendingCategorization, and the column's own comment
            // for why an explicit marker rather than `category IS NULL`.
            CategorizationAttempts = PendingCategorization.Owing,
        };

        // **#92 removed the categorizer call that stood here**, and the paragraph
        // it removed is worth keeping in outline because this reverses an explicit
        // decision rather than filling a gap. #39 categorised before
        // SaveChangesAsync so that the row was written exactly once, and named the
        // alternative -- save first, update when the answer arrives -- along with
        // its costs: two writes, a second code path, and a window in which the API
        // has answered 201 with a category the database does not have. It chose
        // against it because the failure it protects against is one where the
        // user's transaction is lost, and #59's two-second connect budget already
        // bounded the window to two seconds.
        //
        // What changed is not the argument but the number in it. With the rules
        // behind the port a save cost 142 ms and nobody noticed; with a model it is
        // about 2.1 s of somebody's save, every time, and that is the *working*
        // case rather than the broken one. A timeout can be made short; a model
        // answering correctly cannot.
        //
        // So the costs above are now paid on purpose. The window is real and it is
        // seconds rather than milliseconds -- and what is in it is a row that is
        // correct and uncategorised, which is a state this application has handled
        // since #1 because Category has always been allowed to be absent. The
        // failure #39 was protecting against is gone entirely: nothing between here
        // and the response can fail, so a categorizer that is down no longer costs
        // the save anything at all. That is #92's third acceptance test, and this
        // shape gets it for free rather than by tuning a timeout towards it.
        //
        // #63's never-overwrite rule went with the call, and did not disappear: it
        // is in PendingCategorization.Owed, where it guards both the sweep's SELECT
        // and its UPDATE. That is the first place it is not trivially true.
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(cancellationToken);

        // 201 with no Location header, deliberately. Location is optional in
        // RFC 9110, and #3 stops at two endpoints, so there is no GET-by-id for
        // it to point at -- a URL that 404s would be worse than an absent header.
        // The cast picks the string? overload; without it the call is ambiguous.
        return TypedResults.Created((string?)null, ToResponse(transaction));
    }

    /// <summary>#67. What the categorizer would say, for a transaction nobody has saved.</summary>
    // The only endpoint in this application that touches neither Postgres nor
    // anything on disk: it takes three fields, asks one question and answers it.
    // Worth knowing because it is also the only one whose behaviour can be checked
    // end to end without a database -- and because it means a suggestion cannot
    // slow, lock or fail a save. #67's "a suggestion must never delay or block the
    // save" is answered structurally here rather than by care: there is no save on
    // this path to delay.
    //
    // **It does not remember the answer, and the save asks again.** So what the
    // screen shows and what the row ends up holding are two calls, not one. The
    // alternative was to let the client send back the category it was shown, which
    // is one call instead of two and guarantees they agree -- and it lost on
    // provenance: a client that can send a category can send a source, and a row
    // claiming `model` because a browser said so is precisely the hole
    // transactions.category_source was added in #59 to close. UpdateCategoryRequest
    // makes the same call in the same words, and the rules agreeing is what makes
    // the two answers agree in practice: the deployed predictor is deterministic
    // (#61), and the model's is keyed on exactly these three fields in the cache
    // #65 built.
    //
    // The currency is uppercased here, which looks cosmetic and is not: CreateAsync
    // does it before it asks, so a preview that did not would send a different
    // string, miss that cache and be able to answer differently from the save that
    // follows it.
    private static async Task<Ok<CategorySuggestionResponse>> SuggestCategoryAsync(
        CategorySuggestionRequest request,
        CategorizerClient categorizer,
        CancellationToken cancellationToken)
    {
        var answer = await categorizer.PreviewCategoryAsync(
            request.Description,
            request.Amount,
            request.Currency.ToUpperInvariant(),
            cancellationToken);

        // Answered whole rather than reduced to a category, because the absence of
        // one has two meanings on this path and only one of them is worth showing.
        // See CategorySuggestionResponse.
        return TypedResults.Ok(new CategorySuggestionResponse(answer.Category, answer.Source));
    }

    /// <summary>One page of the list, newest first. #95.</summary>
    // **`limit` and `cursor` are optional and the endpoint is unchanged for a caller
    // that sends neither** -- it answers the newest fifty rather than every row there
    // is, which is the whole of the change and the whole of the risk. A client that
    // does not follow `nextCursor` now describes fifty transactions as though they
    // were the table, which is why the response shape changed with it: an envelope is
    // a compile error in the client, and a bare array with fewer rows in it is not.
    //
    // A bad cursor is a 400 and not an empty page. It is the same argument
    // ClampPageSize makes in the other direction: a limit of 5,000 has an obvious
    // reading and is answered, and a token that does not parse names a place that
    // does not exist -- answering it with zero rows would look exactly like reaching
    // the end of the list, which is the one wrong answer that is indistinguishable
    // from a right one.
    private static async Task<Results<Ok<TransactionPage>, ProblemHttpResult>> ListAsync(
        AppDbContext db,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        TransactionCursor? after = null;

        // Empty is read as absent rather than as a broken token, and the reason is
        // the parameter beside it: `?limit=` binds to null because the framework
        // reads an empty value for an int? that way, so `?cursor=` refusing would
        // make two query parameters of one endpoint disagree about what an unset
        // variable in a client's query string means. TransactionCursor.TryParse
        // answers false for it either way -- the reading is the endpoint's, not the
        // token's.
        if (!string.IsNullOrEmpty(cursor) && !TransactionCursor.TryParse(cursor, out after))
        {
            return TypedResults.Problem(
                detail: "The cursor is not one this API issued. Ask for the list again without it.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var pageSize = TransactionPaging.ClampPageSize(limit);

        // The ordering and the cursor's comparison live together in
        // TransactionPaging, because they are one decision written twice: a sort key
        // added here without the matching condition in the cursor repeats rows, and
        // the condition without the key skips them. Neither is visible in a table
        // small enough to check by eye, which is the reason they are not written
        // inline in this handler.
        var page = TransactionPaging.NewestFirst(db.Transactions, after)

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
                t.CategorySource,

                // #92, and it must say the same thing as ToResponse below. The two
                // spellings cannot be shared: this one is an expression tree that
                // Postgres evaluates, so it cannot call a method, and that one runs
                // in memory over an entity. TransactionResponseTests pins them to
                // each other for the four states that matter.
                //
                // The null is spelled out here and would be redundant in C#, where
                // `null < 30` is simply false. In SQL it is *unknown*, and a
                // projection that hands unknown to a non-nullable bool is a
                // difference that shows up only against a real database -- which is
                // the same class of trap as #88's leading slash: correct on the
                // machine it was written on, wrong where it runs.
                t.CategorizationAttempts != null
                    && t.CategorizationAttempts < PendingCategorization.DefaultMaxAttempts,

                t.CreatedAt));

        var (rows, hasMore) = await TransactionPaging.PageAsync(page, pageSize, cancellationToken);

        // Built from the last row of the page rather than counted, which is what
        // makes the token survive everything that happens to the table underneath
        // it: rows inserted above the reader are rows they have already passed, and
        // the row this was built from can be deleted without the position it names
        // going anywhere. That is the property OFFSET does not have, and #95's second
        // trap is exactly the failure of not having it.
        var nextCursor = hasMore ? TransactionCursor.Encode(rows[^1]) : null;

        return TypedResults.Ok(new TransactionPage(rows, nextCursor));
    }

    /// <summary>What one month cost, added up by Postgres. #95.</summary>
    // **#68 did this in the browser and said what would end it**: "the day
    // GET /api/transactions grows a page, this component keeps rendering and starts
    // describing the page it was handed rather than the month -- with no error, no
    // warning, and a number that looks entirely plausible. The fix on that day is a
    // sum on the server in decimal, not a bigger page." This is that day and that
    // fix.
    //
    // What is given up is the property that made #68's first acceptance test true by
    // construction: the totals and the rows below them came from one array, so they
    // could not disagree. They are two queries now, made a few milliseconds apart,
    // and a transaction saved between them shows in one and not the other until the
    // next reload. That is a real regression and it is the cheaper half of the trade
    // -- the alternative is a client adding up a page and calling it a month.
    //
    // The month is required rather than defaulted to the server's own clock, which is
    // MonthRange's paragraph: OccurredAt is a plain day with no zone (#17), so which
    // month a row falls in is a question only the reader's calendar can answer.
    private static async Task<Results<Ok<MonthSummaryResponse>, ProblemHttpResult>> MonthSummaryAsync(
        AppDbContext db,
        CancellationToken cancellationToken,
        string? month = null)
    {
        if (!MonthRange.TryParse(month, out var first, out var next))
        {
            return TypedResults.Problem(
                detail: "Ask for a month as four digits, a dash and two digits: 2026-08.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // GroupBy translated into SQL, which is the whole point -- the rows never
        // leave Postgres and what comes back is one line per (currency, category)
        // pair, of which there are at most eleven plus one per currency.
        //
        // No ownership condition, and that is the property #89 chose an endpoint over
        // a psql script for: AppDbContext's global filter puts it there, so a
        // summary cannot be made to add up somebody else's spending by forgetting a
        // clause. A hand-written WHERE would look exactly as right.
        //
        // The range is half-open on occurred_at and therefore served by the same
        // index the list walks, with owner_id pinned by equality above it.
        var groups = await db.Transactions
            .Where(t => t.OccurredAt >= first && t.OccurredAt < next)
            .GroupBy(t => new { t.Currency, t.Category })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.Category,

                // Sum of a decimal, so Postgres adds numeric(18,2) values in
                // numeric. That is the sentence money.ts exists to work around in
                // JavaScript, and it is free here.
                Total = g.Sum(t => t.Amount),
                Count = g.Count(),
            })
            .ToListAsync(cancellationToken);

        // The shaping and the ordering happen in memory, deliberately. There are a
        // dozen rows by this point, so ordering them in SQL would buy nothing and
        // would put #68's two ordering rules into a query where the reason for them
        // cannot be written beside them. The rules are the ones that were in
        // summary.ts, moved rather than changed.
        var currencies = groups
            .GroupBy(row => row.Currency)
            .Select(byCurrency => new CurrencyTotals(
                byCurrency.Key,

                // Largest first, then by name, so two equal totals are ordered by a
                // rule rather than by whichever row the grouping met first. The
                // uncategorised row takes its place among the others by its own total
                // and is pinned to neither end: it is a row like any other.
                [.. byCurrency
                    .Select(row => new CategoryTotal(row.Category, row.Total, row.Count))
                    .OrderByDescending(row => row.Total)
                    .ThenBy(row => row.Category ?? UncategorisedSortsLast, StringComparer.Ordinal)],

                byCurrency.Sum(row => row.Total),
                byCurrency.Sum(row => row.Count)))

            // By how many transactions, and deliberately not by the total. Ordering
            // the currency blocks by their totals would put 500 MDL above 400 EUR,
            // which is the same mistake as adding them: it treats two numbers in
            // different units as comparable, and nothing in this project converts
            // between them. A count is a count in any currency.
            .OrderByDescending(currency => currency.Count)
            .ThenBy(currency => currency.Currency, StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok(new MonthSummaryResponse(currencies));
    }

    /// <summary>Where the uncategorised row sorts when totals tie.</summary>
    // A sort key and never a value: nothing renders this string, and the client
    // decides what the absent category is called (#68 calls it "Uncategorised").
    // Sorting nulls with a word that begins with a tilde puts them after every real
    // category name in an ordinal comparison, which is the arbitrary-but-stable half
    // of a tiebreak -- the total decided the order already, and this only has to be
    // the same answer twice.
    private const string UncategorisedSortsLast = "~";

    /// <summary>How many rows a backfill would mark. #95.</summary>
    // The count #93 put on the button, which used to be arithmetic over the loaded
    // rows and cannot be any more -- a paged client would offer to categorise the
    // fifty on screen while the server marked every uncategorised row in the table.
    // At about 0.62 US cents a call (#87) that is a bill discovered afterwards, which
    // is the exact trap #93's third bullet exists to prevent.
    //
    // **It counts through the same expression the POST acts through**, which is the
    // only thing that makes the number on the button true. Two predicates that mean
    // the same thing are two predicates that drift, and the drift here is silent and
    // is money.
    private static async Task<Ok<BackfillCountResponse>> CountBackfillableAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // CountAsync, so Postgres answers with a number rather than sending rows for
        // this process to length-check. It reads the same index the sweep does and
        // is scoped by the global filter without asking, the way the POST is.
        var count = await db.Transactions
            .Where(PendingCategorization.Backfillable(PendingCategorization.DefaultMaxAttempts))
            .CountAsync(cancellationToken);

        return TypedResults.Ok(new BackfillCountResponse(count));
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
    /// <summary>#63. Corrects one category, and records that a person did it.</summary>
    // The row is loaded rather than updated in place with ExecuteUpdateAsync. That
    // would be one round trip instead of two and is the wrong trade here: the
    // response has to carry the stored row back so the client can put it on screen
    // without asking for the whole list again, and "did it exist" and "was it
    // yours" both come out of the same SELECT for free.
    //
    // No ownership check appears in this method, and its absence is the design
    // rather than an omission. AppDbContext applies a global query filter on
    // OwnerId, so another account's row is not found at all and this answers 404 --
    // which is also the right status to answer on purpose: a 403 would confirm that
    // the id exists, and there is no reason to let one account enumerate another's
    // transactions by watching which ids are refused differently.
    private static async Task<Results<Ok<TransactionResponse>, NotFound>> UpdateCategoryAsync(
        Guid id,
        UpdateCategoryRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (transaction is null)
        {
            return TypedResults.NotFound();
        }

        // The two columns are written from one value, the way #59 established, so
        // they cannot disagree: a source exists exactly when a category does. That
        // is what makes clearing set *both* to null rather than leaving "human"
        // behind on an empty category.
        //
        // What that costs, and it was decided rather than overlooked: a row a
        // person deliberately cleared -- "I do not know either", which is a real
        // answer and the same abstention the rules baseline produces -- is
        // indistinguishable afterwards from a row nothing has ever touched. So a
        // future backfill would re-predict over it, which is a hole in the
        // never-overwrite rule above. The alternative was to break the invariant
        // and store `category = null, source = human`, and it lost on making a
        // property that is currently checkable in one line of SQL into a special
        // case that every later query has to know about. Revisit it when something
        // actually re-categorises rows, which is the change that makes the hole
        // cost anything.
        transaction.Category = request.Category;
        transaction.CategorySource = request.Category is null ? null : CategorySources.Human;

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ToResponse(transaction));
    }

    /// <summary>#94. Corrects the four fields a person typed, and re-asks if it has to.</summary>
    // **The concurrency question #94's first trap asks, answered rather than
    // shrugged at: last write wins, deliberately, and there is no token.**
    //
    // What #63 refused was a *hidden* write. Its PATCH exists to change one field,
    // so any other field travelling in that body arrives from whatever copy of the
    // row the screen happened to be holding -- somebody correcting a category in a
    // tab opened an hour ago would silently rewrite an amount they never looked at.
    // That is a write nobody made a decision about, and no amount of care at the
    // call site prevents it.
    //
    // This endpoint is the opposite shape. Every field it writes is a field
    // somebody read on their screen and chose to leave alone or to change; the form
    // is prefilled from the row and Save is an act. A stale value can still be
    // saved here -- if the row changed underneath an open form -- but it is a value
    // the person was looking at, which is the difference between a mistake and an
    // invisible one.
    //
    // **What that costs, exactly:** two tabs open on one row, both edited, and the
    // second save wins with no warning. That is the whole of it, on an application
    // one person uses weekly, in an account only they can sign in to.
    //
    // **What the fix would be, so the next reader does not have to find it.**
    // Postgres gives every row a system column, `xmin`, that changes on every
    // update, and Npgsql maps it as a concurrency token with
    // `UseXminAsConcurrencyToken()` -- **no migration and no new column**, which is
    // unusual enough to be worth writing down. It buys nothing until the version
    // also travels to the client and back, which is a field on TransactionResponse,
    // a field on this request, and a 409 the client has to have something to say
    // about. That is the price, and it is the reason it is not here: it is
    // machinery for a race this application cannot currently run.
    private static async Task<Results<Ok<TransactionResponse>, NotFound>> UpdateAsync(
        Guid id,
        UpdateTransactionRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // Loaded rather than updated in place with ExecuteUpdateAsync, the same call
        // UpdateCategoryAsync makes and for the same reasons: the response carries
        // the stored row back so the client can put it on screen without asking for
        // the whole list again, and "did it exist" and "was it yours" both fall out
        // of one SELECT. There is a third reason here -- the re-prediction decision
        // below needs the old values to compare against.
        //
        // No ownership check, and its absence is the design: AppDbContext's global
        // query filter means another account's row is not found at all, so this
        // answers 404. That is #94's second acceptance test, and #63 already
        // established why 404 rather than 403 -- a 403 confirms the id exists, which
        // lets one account enumerate another's transactions by watching which ids
        // are refused differently.
        var transaction = await db.Transactions
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (transaction is null)
        {
            return TypedResults.NotFound();
        }

        // Uppercased before it is compared as well as before it is stored, or "eur"
        // would read as a change from "EUR" and spend a model call on a row nothing
        // about has moved. Same call CreateAsync makes, for the reason written
        // there.
        var currency = request.Currency.ToUpperInvariant();

        var asked = CategorizerQuestion.About(transaction);
        var asking = new CategorizerQuestion(request.Description, request.Amount, currency);

        transaction.OccurredAt = request.OccurredAt;
        transaction.Amount = request.Amount;
        transaction.Currency = currency;
        transaction.Description = request.Description;

        // **#94's second trap: whether an edit re-predicts is a decision.** It does,
        // when the edit changed something a predictor reads, and never otherwise --
        // so fixing a mistyped year is free and fixing a misspelled shop is not.
        //
        // The old category is cleared rather than kept until a new one arrives.
        // Keeping it leaves a word on the screen that was predicted from text
        // nobody can see any more, which is a quieter kind of wrong than a blank
        // that says "Categorizing..." for five seconds. Both columns go together,
        // which is #59's invariant: a source exists exactly when a category does.
        //
        // **MayOverwrite is what makes a human label survive, and it is
        // load-bearing in a second way that is easy to miss.** The sweep's own
        // predicate excludes rows whose source is `human`, so marking one as owing
        // a category would produce a row that owes something nothing will ever
        // collect -- `categoryPending` true for ever, and a client polling until its
        // budget runs out. The guard is not only about not overwriting a person's
        // judgement; without it this endpoint can write a state the rest of the
        // application cannot get out of.
        //
        // The other half of that decision, said out loud: a row somebody labelled by
        // hand and then edited keeps a label chosen for the old text. That is
        // correct rather than tolerated -- the label is about what was bought, and
        // a different spelling of the shop's name does not un-say it. Clearing it is
        // one dropdown away if they disagree.
        if (asking != asked && CategorySources.MayOverwrite(transaction.CategorySource))
        {
            transaction.Category = null;
            transaction.CategorySource = null;
            transaction.CategorizationAttempts = PendingCategorization.Owing;
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ToResponse(transaction));
    }

    /// <summary>#94. Removes one row, for good.</summary>
    // **Hard delete, and #94's third trap is the argument that decides it.** A
    // soft-deleted row is still in the table, and the import's duplicate detection
    // reads the table -- so re-importing the line that was deleted by mistake would
    // be skipped as a duplicate of a row that is not on the screen. That failure
    // reads as a broken import, names the right line number for the wrong reason,
    // and is unfixable from the interface.
    //
    // The rest of the cost is the shape a soft delete puts on everything else. The
    // ownership filter is already global and unforgettable, which is the property
    // #89 chose an endpoint over a psql script for; a second condition beside it is
    // another thing every future query gets right by inheritance and every
    // ExecuteUpdate, every raw statement and every `IgnoreQueryFilters` call -- and
    // the sweep is already one of those -- has to remember. The export would have to
    // exclude them, or a row deleted as a mistake would go on training an eval set.
    //
    // What that gives up is an undo, and it is given up knowingly: there is none,
    // and the row is gone from Postgres the moment this returns. The confirmation
    // step lives on the client, which is where the misclick is; #94 asks for it in
    // as many words, and it is the only thing standing between a stray tap and a
    // year of history.
    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // ExecuteDelete rather than loading the row and calling Remove: there is
        // nothing to read, nothing to return, and no decision to make from the old
        // values -- which is exactly the case UpdateAsync above is not.
        //
        // **The global query filter applies to it**, the same way it applies to a
        // SELECT and the same way #93's backfill depends on it applying to an
        // ExecuteUpdate. So another account's row matches nothing, this answers 404,
        // and no ownership condition appears anywhere in the statement. That is the
        // property worth stating rather than assuming: the alternative -- a
        // hand-written `WHERE id = @id AND owner_id = @owner` -- is one clause away
        // from being a way to delete somebody else's row, and it would look exactly
        // right.
        //
        // A row the sweep is in the middle of asking about is safe by construction:
        // its UPDATE is guarded by the same primary key, so it matches nothing and
        // writes nothing. Nothing has to be co-ordinated for that.
        var deleted = await db.Transactions
            .Where(transaction => transaction.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        // 404 and never 403, for the reason UpdateAsync and #63 both record. It is
        // also what a second DELETE of the same id answers, which is the honest
        // reading rather than an inconvenience: the row is not there, and a client
        // that shows the same message either way is right.
        return deleted == 0
            ? TypedResults.NotFound()
            : TypedResults.NoContent();
    }

    /// <summary>How many rows the file holds, so the client can say so without parsing it.</summary>
    // A header rather than a field in a JSON envelope, because the body has to stay a
    // CSV file: anything wrapping it makes `curl -OJ` produce something that is not
    // the file, and puts the whole export through JSON escaping for the sake of one
    // integer. Counting the lines on the other side is the alternative and is wrong
    // for a reason that only shows up in the rows that matter -- a quoted description
    // may contain a newline, so line count and row count are not the same number, and
    // the client would have to own a second CSV parser to find out.
    //
    // Same-origin, so nothing has to be exposed for the browser to read it: the
    // Access-Control-Expose-Headers rule applies to cross-origin responses, and this
    // client is served by this application (#20).
    private const string RowCountHeader = "X-Labelled-Rows";

    /// <summary>#89. The rows a person labelled, as the five columns the eval set holds.</summary>
    // **Where this lives was the decision, and the alternative was a psql query
    // written into docs/.** That is genuinely less code -- no route, no writer, no
    // client -- and #37 has already established that this machine can reach the
    // deployed database. It lost on the trap the issue names second. The export has
    // to be scoped to one owner, and in psql that scoping is a WHERE clause somebody
    // types, against an owner id they first have to look up; here it is
    // AppDbContext's global query filter, which is applied to a query that does not
    // mention ownership and cannot forget to. The failure modes are not symmetrical:
    // a forgotten clause in a hand-typed query exports every account's rows into a
    // file that looks exactly right, and #52's bug is this repository's evidence that
    // that class of mistake is invisible from the outside.
    //
    // Two smaller things went the same way. psql is a dependency this repository
    // declined once already, in #37, for the same "another thing to install and
    // another place the connection string has to arrive" reason -- and the labelling
    // it exports is done in a browser, so a route that needs a terminal and a
    // connection string is a different act from the one that produced the rows. And
    // the five columns are written down once, in LabelledCsv, next to the four the
    // import reads, rather than in a SQL string in a document that nothing compiles.
    //
    // What the route taken costs, said plainly: about ninety lines and a screen, for
    // something one person runs a handful of times a year. That is the honest size of
    // it, and it is the reason this was worth arguing rather than assuming.
    private static async Task<ContentHttpResult> ExportLabelledAsync(
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var rows = await db.Transactions
            // **The one WHERE clause this issue exists for**, and it is a named rule
            // in LabelledRows rather than a lambda written here -- a `model` row in
            // the eval set is the predictor grading its own past answers, and the
            // number afterwards means nothing while the file looks exactly right.
            // The comment on that field is the long version, including what a test
            // can hold about it and what it cannot.
            .Where(LabelledRows.ByHand)

            // Ascending, which is the opposite of ListAsync and is not an oversight.
            // evals/transactions.csv is in date order, and this file is meant to be
            // appended to it -- so ascending keeps the result sorted, where the
            // screen's newest-first would interleave backwards. CreatedAt is the same
            // tiebreak for the same reason as there: OccurredAt is a day, several rows
            // share one, and without a second key their order is whatever Postgres
            // finds cheapest. Two exports of an unchanged table are then byte-identical,
            // which is what makes a diff of two of them mean something.
            .OrderBy(transaction => transaction.OccurredAt)
            .ThenBy(transaction => transaction.CreatedAt)

            // The null-forgiving operator is what the Where above earns. EF translates
            // the projection rather than running it, so the compiler cannot see that
            // the filter has already excluded the nulls.
            .Select(transaction => new LabelledRow(
                transaction.OccurredAt,
                transaction.Amount,
                transaction.Currency,
                transaction.Description,
                transaction.Category!))
            .ToListAsync(cancellationToken);

        // A row corrected twice exports once, with the last label -- and it is worth
        // saying out loud that nothing here arranges that. PATCH updates the row in
        // place and there is no history table, so "the latest label" is the only
        // label there is. The day corrections are journalled, this query grows a
        // DISTINCT ON and this comment becomes wrong.
        var csv = LabelledCsv.Render(rows);

        var fileName = LabelledCsv.FileName(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        http.Response.Headers[RowCountHeader] = rows.Count.ToString(CultureInfo.InvariantCulture);

        // Encoding passed explicitly, which is what puts `; charset=utf-8` on the
        // content type -- and what keeps the BOM off, since this writes the encoded
        // bytes rather than the preamble. A BOM would be read by evals/score.py,
        // which opens the set as utf-8-sig, and would be one more difference between
        // the exported file and the one it is appended to.
        return TypedResults.Text(csv, CsvContentType, Encoding.UTF8);
    }

    /// <summary>#93. Puts the rows that have no category into the sweep's queue.</summary>
    // **What this is for is a gap the sweep cannot reach on its own.** #92's sweep
    // only ever sees rows something marked as owing a category, and the create path
    // is the only thing that marks them. So #62's imported rows -- which arrive with
    // no category by design, because one call per row against a service that is not
    // there is a request that runs for minutes -- were never going to be categorised
    // by anything. Neither were the rows the sweep gave up on while the categorizer
    // was down, nor the rows that predate the column.
    //
    // **The import deliberately does not do this by itself**, which is the decision
    // this endpoint exists to make possible. #92 wrote that marking imported rows was
    // "one property assignment away", and it is; what that assignment does not do is
    // #93's third trap -- "whatever runs this should know how many rows it is about
    // to pay for before it starts". A three-hundred row file is three hundred model
    // calls at 0.62 US cents each (#87), and a person who has just imported a year of
    // statements should be told that number and press something, rather than discover
    // it on a bill. So the count is on the screen before the button is, and the button
    // is this.
    //
    // It also reaches what an automatic mark could not: a row abandoned at the cap
    // was already marked once, so nothing about the import would ever look at it
    // again.
    private static async Task<Ok<BackfillResponse>> BackfillCategoriesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // ExecuteUpdate, so this is one statement rather than a table read followed
        // by a change tracker full of entities nobody wants -- and so the predicate
        // is evaluated by Postgres against the rows as they are now. That matters
        // more than the allocation: a correction made while this was in flight is a
        // row this must not claim, and an in-memory guard would be looking at a
        // photograph.
        //
        // **No ownership condition, and that is the point rather than an omission.**
        // AppDbContext's global query filter puts it there, and EF applies it to
        // ExecuteUpdate the same way it applies it to a SELECT -- which is the
        // property #89 chose an endpoint over a psql script for. A hand-written
        // UPDATE that forgot the clause would queue every account's rows and bill one
        // person for another person's spending, and it would look exactly right.
        //
        // The default cap rather than the configured one, knowingly, and for the same
        // reason ToResponse reads the default: threading IConfiguration through here
        // to decide which abandoned rows count as abandoned buys a handful of rows in
        // or out of one run of a chore. The cap that matters is the one the sweep
        // applies.
        var marked = await db.Transactions
            .Where(PendingCategorization.Backfillable(PendingCategorization.DefaultMaxAttempts))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    transaction => transaction.CategorizationAttempts, PendingCategorization.Owing),
                cancellationToken);

        // Nothing else is returned, and in particular not the rows. The client
        // already holds every transaction -- there is no paging (#3) -- and it asks
        // for the list again anyway, because the sweep will have changed rows this
        // response could not know about by the time it arrives.
        return TypedResults.Ok(new BackfillResponse(marked));
    }

    private static TransactionResponse ToResponse(Transaction transaction) => new(
        transaction.Id,
        transaction.OccurredAt,
        transaction.Amount,
        transaction.Currency,
        transaction.Description,
        transaction.Category,
        transaction.CategorySource,

        // #92. Owed *and* still within the cap -- the same pair of conditions
        // PendingCategorization.Owed puts in the WHERE clause, so the screen says
        // "a category is coming" exactly when the sweep would still go and get one.
        // A row that has been given up on says false and reads as an ordinary
        // uncategorised row, which is what it is.
        //
        // The null is spelled out although C# would answer false for it anyway --
        // `null < 30` is false, no check needed. It is written because ListAsync
        // above has to spell it out, in SQL, where the same expression is *unknown*
        // rather than false, and two projections of one field that disagree in
        // shape are two projections somebody will eventually make disagree in
        // meaning.
        //
        // **A mutation sweep confirmed no test can kill the removal of this line,
        // and that is correct rather than a gap.** Deleting it changes nothing this
        // process does: it is an equivalent mutant, kept for the symmetry above and
        // not for behaviour. Writing a test that appeared to catch it would be
        // asserting that C# evaluates `null < 30` the way C# evaluates it.
        //
        // The default rather than the configured cap, knowingly. Threading
        // IConfiguration into a static projection to get a spinner right buys a
        // client that polls a few extra times and then stops; the cap itself is
        // applied where it matters, in the sweep.
        transaction.CategorizationAttempts != null
            && transaction.CategorizationAttempts < PendingCategorization.DefaultMaxAttempts,

        transaction.CreatedAt);
}
