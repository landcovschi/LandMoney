using Microsoft.AspNetCore.Http.HttpResults;

namespace LandMoney.Web.Api;

/// <summary>#63. The closed list, served so the client does not have to hold a copy.</summary>
// One endpoint, and the reason it exists is not that a client cannot hard-code
// eleven strings -- it obviously can, and did for about an hour. It is that the
// dropdown and the validation must be the same list or a correction can be offered
// and then refused, and the cheapest way to guarantee that is for there to be one
// list with the other end reading it. The remaining copy is Python's, and
// CategoriesTests pins this array to it.
//
// A group of its own rather than /api/transactions/categories, because the eleven
// are not a sub-resource of a transaction: nothing about them belongs to one, and
// the day something else needs a category -- a budget, a filter -- it would be
// reading a list out of another feature's URL space. The cost is the extra
// RequireAuthorization in Program.cs, which is a line that has to be remembered;
// AuthorizationTests asserts it rather than trusting that it was.
public static class CategoryEndpoints
{
    /// <summary>Registers /api/categories. Called from Program.cs.</summary>
    public static RouteGroupBuilder MapCategoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/categories");

        // Authorized, although the list is in a public repository and in
        // docs/evals.md, so this is not protecting a secret. It is that every
        // endpoint under /api that is not part of signing in requires a session,
        // and an exception would need a reason better than "this one is harmless".
        //
        // No caching headers. The list changes when somebody edits two files and
        // deploys, so a max-age would be a promise about a deployment rather than
        // about the data; the client asks once per page load, which is a request
        // every few hours against a body of about 120 bytes.
        group.MapGet("/", ListCategories);

        return group;
    }

    private static Ok<IReadOnlyList<string>> ListCategories() => TypedResults.Ok(Categories.All);
}
