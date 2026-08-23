using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;

namespace LandMoney.Web.Tests.Api;

/// <summary>Builds the <see cref="ValidationContext"/> a single attribute is handed.</summary>
// A ValidationAttribute is normally reached through Validator, which builds this
// object out of the property being checked. Testing one attribute on its own means
// building it here instead, and two of its fields carry weight:
//
//   MemberName  -- what the attribute copies into ValidationResult.MemberNames,
//                  and therefore the key the 400 body files the message under
//   DisplayName -- what the message reads as. It defaults to the *instance* type
//                  name, so leaving it unset makes every message begin "Object".
//
// A real ServiceProvider rather than a hand-written IServiceProvider, because that
// is what HttpContext.RequestServices is in production and the point of these
// tests is that the same lookup works. It is not disposed: it holds one instance
// the test itself created, and the test process is about to end.
internal static class ValidationContexts
{
    public static ValidationContext ForMember(string memberName, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();

        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        // The instance is a bare object. Nothing that runs here reads it: a
        // ValidationAttribute is given the value, not the object it came off.
        return new ValidationContext(new object(), services.BuildServiceProvider(), items: null)
        {
            MemberName = memberName,
            DisplayName = memberName,
        };
    }

    /// <summary>A context carrying no service provider at all, as bare Validator calls produce.</summary>
    public static ValidationContext WithNoServices(string memberName) =>
        new(new object())
        {
            MemberName = memberName,
            DisplayName = memberName,
        };
}
