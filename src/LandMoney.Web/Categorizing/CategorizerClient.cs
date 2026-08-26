using System.Net.Http.Json;
using System.Text.Json;
using LandMoney.Web.Models;

namespace LandMoney.Web.Categorizing;

/// <summary>Asks the Python categorizer for a category, and never lets it stop a save.</summary>
// A typed client -- registered with AddHttpClient&lt;CategorizerClient&gt;, so the
// HttpClient handed in is a short-lived wrapper over a pooled, rotated
// HttpMessageHandler. What that buys is the thing a `new HttpClient()` inside a
// handler gets wrong in both directions at once: one per request exhausts
// sockets, and one static instance never notices a DNS change. Under compose the
// second is not academic -- `categorizer` resolves to whatever address the
// container has after it is recreated.
//
// **This class exists to fail quietly.** #39: categorising must never block
// saving, because a transaction is the user's data and a category is a guess
// about it. Every failure below is caught, logged and turned into null, which is
// why Category has been nullable since #1. The only exception it lets past is the
// caller's own cancellation -- see the `when` clause.
public sealed class CategorizerClient(HttpClient http, ILogger<CategorizerClient> logger)
{
    // The path is absolute on purpose. HttpClient resolves a relative path
    // against BaseAddress with URI rules rather than string concatenation: with a
    // base of "http://categorizer:8000/api" and a relative "categorize", the last
    // segment of the base is *replaced* and "/api" is silently gone. A leading
    // slash builds the request from the authority alone, which is the behaviour
    // that does not depend on whether someone remembered a trailing slash in
    // configuration.
    private const string Path = "/categorize";

    // JsonSerializerDefaults.Web is what ASP.NET Core uses: camelCase names and
    // case-insensitive reads. The System.Net.Http.Json helpers already default to
    // it, so this is written out rather than relied upon -- the default is a fact
    // about the library's version, and the contract is a fact about the service.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The suggested category, or null if there is not one to be had.</summary>
    /// <remarks>
    /// Null covers three different events and the caller treats them the same: the
    /// rules abstained, the service answered something unusable, or it was not
    /// there at all. Only the first is visible in a response, and nothing stores
    /// which it was.
    /// </remarks>
    public async Task<string?> SuggestCategoryAsync(
        string description,
        decimal amount,
        string currency,
        CancellationToken cancellationToken)
    {
        // No base address means no categorizer is configured, which Program.cs
        // treats as a legal state rather than a startup failure -- see the long
        // comment there, and the deploy it broke. Without this check the call
        // below throws InvalidOperationException ("An invalid request URI was
        // provided"), which is not one of the three the catch blocks expect, so
        // it would escape and turn a create request into a 500. That is the exact
        // shape this class exists to prevent, arriving through configuration
        // instead of through the network.
        if (http.BaseAddress is null)
        {
            return null;
        }

        try
        {
            using var response = await http.PostAsJsonAsync(
                Path, new CategorizeRequest(description, amount, currency), Json, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately not read as a problem document. A 422 here means
                // this application sent a body the service refused, which is a bug
                // on this side rather than anything to show a user -- the status
                // is what a log needs in order to find it.
                logger.LogWarning(
                    "Categorizer answered {StatusCode}; storing the transaction with no category.",
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<CategorizeResponse>(Json, cancellationToken);

            // Abstention. A 200 with a null category is the baseline working as
            // designed -- it declines on roughly a third of the labelled set -- so
            // it is not a warning and not an error.
            if (body?.Category is not { Length: > 0 } category)
            {
                return null;
            }

            // The one guard here that is not about the network, and the one that
            // would otherwise break the promise this class exists for.
            // Transaction.Category is MaxLength(100); a service answering
            // something longer would make SaveChangesAsync throw, and the
            // transaction the user typed would be lost to a failed guess about it.
            // Refusing the answer keeps the failure inside the part that is
            // allowed to fail.
            if (category.Length > Transaction.CategoryMaxLength)
            {
                logger.LogWarning(
                    "Categorizer answered a category of {Length} characters, over the {Max} the column holds; "
                    + "storing the transaction with no category.",
                    category.Length, Transaction.CategoryMaxLength);
                return null;
            }

            logger.LogInformation(
                "Categorizer suggested {Category} by {Source}.",
                category, body.Source ?? "an unnamed source");
            return category;
        }
        // The timeout, and recognising it takes the `when` clause. HttpClient
        // implements Timeout by cancelling the request, so what surfaces is
        // TaskCanceledException -- the same exception the caller's own token
        // produces, which is why this is a famously confusing thing to read in a
        // log. The token is what tells them apart: if it is not cancelled, nobody
        // asked for this, so it was the clock.
        //
        // The other half matters more than the message. When the token *is*
        // cancelled the caller has gone, and swallowing that here would carry on
        // and save a transaction for a request that no longer exists.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Categorizer did not answer within {Timeout}; storing the transaction with no category.",
                http.Timeout);
            return null;
        }
        // Unreachable, refused, DNS failure, connection reset. The expected one
        // under compose is the service being stopped, which is the acceptance test
        // #39 names.
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception, "Categorizer is unreachable; storing the transaction with no category.");
            return null;
        }
        // A 200 whose body is not the contract: malformed JSON (JsonException), or
        // a content type the reader will not take, such as an HTML error page from
        // something sitting between here and there (NotSupportedException). Both
        // arrive only after a success status, so neither is caught above.
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogWarning(
                exception, "Categorizer answered something unreadable; storing the transaction with no category.");
            return null;
        }
    }
}
