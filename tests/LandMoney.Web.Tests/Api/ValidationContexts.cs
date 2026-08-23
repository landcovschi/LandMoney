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
//
// The type of the clock parameter is load-bearing, raised in review of #31.
// AddSingleton infers its service type from the argument's *static* type, so
// TimeProvider? is what files the instance under TimeProvider -- the key the
// attribute asks for. Type this FixedTimeProvider instead and the same line
// registers under the concrete type, and GetService(typeof(TimeProvider)) misses.
//
// The review expected that to be silent, with every test quietly exercising the
// fallback and passing anyway. Measured instead of assumed, and it is not: nine
// tests go red. Once the lookup misses, the attribute takes the real clock, and
// the fixed dates this file exists to supply -- 2026-06-15, the 2028 leap day --
// stop agreeing with it. Five do survive, the ones whose dates happen to be
// plausible today as well, so the silence is partial and the suite as a whole
// still fails loudly.
//
// It stays written down because of what those nine failures say. They name dates
// and bounds, and not one of them names the registration that actually broke, so
// the evidence points at PlausibleDateAttribute and the cause is here.
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
