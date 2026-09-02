namespace LandMoney.Web.Categorizing;

/// <summary>The HttpClient the batch call uses, which is a different budget. #93.</summary>
// **This class exists so that the second budget can be registered, and for no other
// reason.** #93's fourth trap: "the .NET client's 8-second budget is per call and is
// wrong for a batch. A separate client, or a separate timeout". The eight seconds of
// #59 was chosen against one model call at about 2.1 s; a hundred rows at a
// concurrency of eight is about twenty-six, and raising the single number would
// re-price the failure that number exists to bound -- #67's preview would then wait
// a minute for a categorizer that is not there, on the path where somebody is typing.
//
// So there are two clients, and this is what makes DI able to tell them apart.
// HttpClient is registered by AddHttpClient once per *typed client*, and a typed
// client is identified by its type -- so a second configuration needs a second type
// to hang off. This one holds nothing and does nothing; CategorizerClient reaches
// through it.
//
// What lost, and both were tried on paper first. **A second CategorizerClient-shaped
// class** duplicates four `catch` blocks whose words are CategorizerOutcome's, and
// two copies of a vocabulary are two copies that drift -- which is the failure #64
// spent an issue preventing. **IHttpClientFactory with two named clients** keeps one
// class and one set of catch blocks, and it changes how CategorizerClient itself is
// registered and constructed, so every test that builds one with an HttpMessageHandler
// -- which is the seam #39 chose and 21 tests use -- would have to learn about a
// factory in order to keep testing something that has not changed.
//
// A third option was rejected on a rule rather than on cost: setting
// HttpClient.Timeout to InfiniteTimeSpan and giving every call its own linked
// CancellationTokenSource. It is the tidiest of the three and it makes "every network
// client has a timeout" a thing somebody has to remember at each call site, which
// CLAUDE.md names as the shape of rule that does not hold.
public sealed class CategorizerBatchHttp(HttpClient http)
{
    /// <summary>The client, with the batch budget on it.</summary>
    // A property rather than inheriting from or wrapping HttpClient. Wrapping means
    // re-declaring the members that are used and hiding the ones that are not, which
    // is a lot of surface to maintain in order to avoid one word at the call site.
    public HttpClient Client => http;
}
