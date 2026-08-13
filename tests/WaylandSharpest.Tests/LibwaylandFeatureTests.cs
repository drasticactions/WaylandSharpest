using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Asserts the run is using the libwayland it was told to use.
/// </summary>
/// <remarks>
/// Pointing <c>LD_LIBRARY_PATH</c> at a build that is not there does not fail:
/// the loader falls back to the system libwayland, every version-gated test
/// calls <c>Assert.Skip</c>, and the run goes green having tested the wrong
/// library. Set <c>WAYLANDSHARPEST_REQUIRE_LIBWAYLAND</c> to the minimum version
/// the run is supposed to have, and that becomes a failure instead.
/// </remarks>
[LibWaylandOnly("It probes the loaded libwayland for symbols, which is what it exists to do.")]
public sealed class LibwaylandFeatureTests : LoopbackHarness
{
    private static readonly Version NamedQueues = new(1, 22, 91);
    private static readonly Version GlobalWithdrawn = new(1, 26);

    [Fact]
    public void The_run_uses_the_libwayland_it_was_told_to()
    {
        var requested = Environment.GetEnvironmentVariable("WAYLANDSHARPEST_REQUIRE_LIBWAYLAND");
        if (string.IsNullOrWhiteSpace(requested))
        {
            Assert.Skip("Set WAYLANDSHARPEST_REQUIRE_LIBWAYLAND to the minimum libwayland this run must load.");
        }

        Assert.True(
            Version.TryParse(requested, out var required),
            $"WAYLANDSHARPEST_REQUIRE_LIBWAYLAND='{requested}' is not a version number.");

        const string Hint =
            "The loaded libwayland is older than required. LD_LIBRARY_PATH probably does not point at a directory "
            + "containing libwayland-*.so, so the loader fell back to the system copy.";

        if (required >= NamedQueues)
        {
            using var queue = Client.CreateQueue("version-probe");
            Assert.True(queue.Name == "version-probe", $"libwayland-client does not support named queues. {Hint}");
        }

        if (required >= GlobalWithdrawn)
        {
            using var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });
            Assert.True(global.SupportsWithdrawnNotification, $"libwayland-server has no withdrawn listener. {Hint}");
            Assert.True(WlFixesSupport.IsSupported, $"libwayland-server cannot service wl_fixes. {Hint}");
        }
    }
}
