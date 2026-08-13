using System.Reflection;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Says why a loopback test class runs against libwayland alone.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LibWaylandOnlyAttribute(string reason) : Attribute
{
    /// <summary>What about this class is specific to libwayland.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// The point of the loopback suite is that both transports pass it. A class
/// that quietly loses its managed twin would take its coverage with it and
/// nothing else would notice, so the pairing is checked here.
/// </summary>
public sealed class TransportMatrixTests
{
    private static IEnumerable<Type> LoopbackClasses =>
        typeof(TransportMatrixTests).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(LoopbackHarness).IsAssignableFrom(t));

    /// <summary>The classes that hold the tests, as opposed to the twins that re-run them.</summary>
    private static IEnumerable<Type> BaseClasses =>
        LoopbackClasses.Where(t => !t.Name.EndsWith("Managed", StringComparison.Ordinal));

    [Fact]
    public void Every_loopback_class_either_has_a_managed_twin_or_says_why_not()
    {
        var missing = new List<string>();

        foreach (var type in BaseClasses)
        {
            if (type.GetCustomAttribute<LibWaylandOnlyAttribute>() is not null)
            {
                continue;
            }

            var twin = LoopbackClasses.FirstOrDefault(t => t.Name == type.Name + "Managed");
            if (twin is null)
            {
                missing.Add(type.Name);
            }
            else if (!twin.IsSubclassOf(type))
            {
                missing.Add($"{twin.Name} does not derive from {type.Name}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "These loopback classes run against libwayland alone, without saying why. Add a managed " +
            $"twin, or a [LibWaylandOnly] reason: {string.Join(", ", missing)}");
    }

    [Fact]
    public void A_reason_for_running_against_libwayland_alone_is_a_sentence()
    {
        foreach (var type in BaseClasses)
        {
            if (type.GetCustomAttribute<LibWaylandOnlyAttribute>() is { } only)
            {
                Assert.True(
                    only.Reason.Length > 20,
                    $"{type.Name} gives '{only.Reason}' as its reason, which does not explain anything.");
            }
        }
    }

    [Fact]
    public void The_managed_transport_covers_as_many_tests_as_libwayland_does()
    {
        foreach (var type in BaseClasses)
        {
            var twin = LoopbackClasses.FirstOrDefault(t => t.Name == type.Name + "Managed");
            if (twin is null)
            {
                continue;
            }

            // The twin adds nothing of its own, so equal counts mean every test
            // of the original runs against both transports.
            Assert.Equal(CountFacts(type), CountFacts(twin));
        }
    }

    private static int CountFacts(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Count(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any());
}
