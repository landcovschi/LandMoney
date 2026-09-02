using System.ComponentModel.DataAnnotations;
using System.Reflection;
using LandMoney.Web.Api;

namespace LandMoney.Web.Tests.Api;

/// <summary>#94. The second way into this table obeys the first one's rules.</summary>
// **#94's fourth trap in one sentence: "any new path must go through the same
// Validator call, or the two ways into this table drift".** CategorySuggestionRequest
// answers that with a copy and a reflection test that fails when the copies
// disagree -- because it deliberately has one fewer field. This record has exactly
// the same four, so it answers it by inheriting, and the tests here are about the
// inheritance actually doing the work rather than about the rules themselves.
//
// The difference matters. Those tests can fail; these ones can only fail if
// somebody breaks the relationship -- which is the point. A rule added to
// CreateTransactionRequest is on this type before anybody remembers there is a
// second way in, and there is no list anywhere that has to be kept up to date.
public class UpdateTransactionRequestTests
{
    // The relationship, stated once. Everything below follows from it, and if this
    // is what somebody changes then the failures underneath are the explanation.
    [Fact]
    public void The_update_request_is_a_create_request()
    {
        Assert.True(typeof(CreateTransactionRequest)
            .IsAssignableFrom(typeof(UpdateTransactionRequest)));
    }

    // The emptiness is the design. A property declared here would be a field the
    // create path does not have and a rule nothing else applies -- which is exactly
    // the drift the inheritance exists to prevent, arriving from the other side.
    [Fact]
    public void It_adds_nothing_of_its_own()
    {
        var declared = typeof(UpdateTransactionRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)

            // A record generates one: `protected virtual Type EqualityContract`. It
            // is the compiler's and not a field of this contract.
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name);

        Assert.Empty(declared);
    }

    // The fields a client may send, named rather than counted, so that adding one
    // to the create path is a decision somebody makes here as well. All four are
    // things a person types and can mistype, which is why all four are editable.
    [Theory]
    [InlineData(nameof(CreateTransactionRequest.OccurredAt))]
    [InlineData(nameof(CreateTransactionRequest.Amount))]
    [InlineData(nameof(CreateTransactionRequest.Currency))]
    [InlineData(nameof(CreateTransactionRequest.Description))]
    public void Every_field_a_person_typed_can_be_corrected(string property)
    {
        Assert.NotNull(typeof(UpdateTransactionRequest).GetProperty(property));
    }

    // **What must never be on it.** #59 added `category_source` so that a guess and
    // a correction could be told apart, and a client that can send either field can
    // file a guess as a correction. #63's PATCH is the only way to set a category
    // and it stamps `human` itself; this endpoint replaces the four typed fields
    // and touches a category only by clearing a prediction it invalidated.
    [Theory]
    [InlineData("Category")]
    [InlineData("CategorySource")]
    [InlineData("Id")]
    [InlineData("CreatedAt")]
    [InlineData("OwnerId")]
    [InlineData("CategorizationAttempts")]
    public void Nothing_the_server_owns_can_be_sent(string property)
    {
        Assert.Null(typeof(UpdateTransactionRequest).GetProperty(property));
    }

    // --- the rules, run rather than reflected over ---------------------------

    // The reflection above says the attributes are the same objects. This says they
    // are actually applied when the derived type is the one being validated, which
    // is a different claim: Validator.TryValidateObject reads the *runtime* type,
    // and inherited attributes are only found because it walks the hierarchy. The
    // day that stops being true, every one of these refusals becomes an accepted
    // request that a database constraint or a Python service refuses instead.
    [Fact]
    public void An_ordinary_correction_is_accepted() => Assert.Empty(Validate(Request()));

    [Theory]
    [InlineData(0)]
    [InlineData(-12.50)]
    public void An_amount_the_column_would_not_hold_is_refused(decimal amount) =>
        Assert.NotEmpty(Validate(Request(amount: amount)));

    [Fact]
    public void A_third_decimal_place_is_refused_here_too() =>
        Assert.NotEmpty(Validate(Request(amount: 12.345m)));

    [Theory]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("1$x")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency) =>
        Assert.NotEmpty(Validate(Request(currency: currency)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_description_with_nothing_in_it_is_refused(string description) =>
        Assert.NotEmpty(Validate(Request(description: description)));

    [Fact]
    public void A_description_longer_than_the_column_is_refused() =>
        Assert.NotEmpty(Validate(Request(description: new string('x', 501))));

    // PlausibleDateAttribute, reached through inheritance, and the bound is the one
    // its own comment predicted this feature would make somebody revisit: five
    // years. It is still five, so a statement older than that cannot be corrected
    // into the table any more than it can be typed into it.
    [Fact]
    public void A_date_the_create_path_would_refuse_is_refused()
    {
        Assert.NotEmpty(Validate(Request(occurredAt: new DateOnly(2016, 1, 1))));
    }

    // The same call ValidationFilter<T> makes and the same call #62's import path
    // makes. Both arguments are load-bearing and both fail silently if dropped:
    // validateAllProperties: true, or everything but [Required] is skipped; and a
    // service provider, or PlausibleDateAttribute never finds a TimeProvider. The
    // provider is left off here deliberately -- the attribute's documented
    // fallback is the system clock, and the date above is wrong under any clock
    // this test could run on.
    private static List<ValidationResult> Validate(UpdateTransactionRequest request)
    {
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        return results;
    }

    private static UpdateTransactionRequest Request(
        DateOnly? occurredAt = null,
        decimal amount = 42.50m,
        string currency = "EUR",
        string description = "linella") =>
        new()
        {
            OccurredAt = occurredAt ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Amount = amount,
            Currency = currency,
            Description = description,
        };
}
