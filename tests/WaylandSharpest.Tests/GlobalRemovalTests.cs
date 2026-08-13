using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Two-phase global removal: unpublish, let in-flight binds resolve, then
/// destroy. Destroying in one step kills any client that sent
/// <c>wl_registry.bind</c> before it processed <c>global_remove</c>, which for a
/// compositor is the output-unplug path rather than an edge case.
/// </summary>
public class GlobalRemovalTests : LoopbackHarness
{
    /// <summary>Runs against libwayland.</summary>
    public GlobalRemovalTests()
    {
    }

    /// <summary>Runs against the transport a twin supplies.</summary>
    protected GlobalRemovalTests(global::Wayland.Server.IWlServerTransport transport) : base(transport)
    {
    }

    [Fact]
    public void Removed_global_disappears_from_registry()
    {
        using var kept = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });
        var going = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        using var registry = Client.GetRegistry();
        var announced = new Dictionary<uint, string>();
        var removed = new List<uint>();
        registry.Global += (_, e) => announced[e.Name] = e.Interface;
        registry.GlobalRemove += (_, e) => removed.Add(e.Name);
        PumpToClient();

        var seatName = announced.Single(g => g.Value == "wl_seat").Key;
        Assert.Empty(removed);

        going.Remove();
        Assert.True(going.IsRemoved);
        PumpToClient();

        Assert.Equal([seatName], removed);

        // A registry created after the removal never sees it at all.
        using var second = Client.GetRegistry();
        var reannounced = new List<string>();
        second.Global += (_, e) => reannounced.Add(e.Interface);
        PumpToClient();

        Assert.Contains("wl_compositor", reannounced);
        Assert.DoesNotContain("wl_seat", reannounced);

        going.Dispose();
    }

    [Fact]
    public void Removing_twice_throws_rather_than_aborting_the_process()
    {
        // wl_global_remove calls wl_abort on a second call, so the managed guard
        // is what keeps this a test failure instead of a crashed test run.
        using var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });
        global.Remove();

        Assert.Throws<InvalidOperationException>(() => global.Remove());
        Assert.True(global.IsRemoved);
    }

    [Fact]
    public void Dispose_after_remove_destroys_exactly_once()
    {
        var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });
        global.Remove();

        global.Dispose();
        global.Dispose();

        Assert.Throws<ObjectDisposedException>(() => global.Remove());
    }

    [Fact]
    public void Remove_and_dispose_disposes_after_the_grace_period()
    {
        var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        // The client holds its registry and never acknowledges, so the withdrawn
        // notification cannot fire: only the grace timer can finish this, which
        // is exactly the case that would leak without it.
        using var registry = Client.GetRegistry();
        var removed = new List<uint>();
        registry.GlobalRemove += (_, e) => removed.Add(e.Name);
        PumpToClient();

        global.RemoveAndDispose(graceMs: 1);
        Assert.True(global.IsRemoved);

        // Still alive right after the call; disposal is deferred to the loop.
        PumpToClient();
        Assert.Single(removed);

        for (var i = 0; i < 20 && !IsDisposed(global); i++)
        {
            Server.EventLoop.Dispatch(20);
        }

        Assert.True(IsDisposed(global), "the grace timer never disposed the global");
    }

    [Fact]
    public void Remove_and_dispose_completes_promptly_when_nobody_is_watching()
    {
        var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        // No registry ever saw it. Where the withdrawn notification exists this
        // finishes synchronously; otherwise the timer still bounds it.
        global.RemoveAndDispose(graceMs: 1);

        for (var i = 0; i < 20 && !IsDisposed(global); i++)
        {
            Server.EventLoop.Dispatch(20);
        }

        Assert.True(IsDisposed(global));
    }

    /// <summary>Disposal is observable only through the guards on the disposed object.</summary>
    private static bool IsDisposed(WlGlobal global)
    {
        try
        {
            _ = global.Version;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [Fact]
    public void Withdrawn_notification_is_immediate_when_no_client_has_seen_the_global()
    {
        var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        if (!global.SupportsWithdrawnNotification)
        {
            global.Dispose();
            Assert.Skip("libwayland-server is older than 1.26 and does not export wl_global_set_withdrawn_listener.");
        }

        var withdrawn = 0;
        global.Withdrawn += () => withdrawn++;

        // Nothing was ever offered, so there is nothing to wait for.
        global.Remove();

        Assert.Equal(1, withdrawn);
        global.Dispose();
    }


}

/// <summary>The same tests, against the managed transport.</summary>
[Trait("Transport", "Managed")]
public sealed class GlobalRemovalTestsManaged() : GlobalRemovalTests(new global::Wayland.Server.ManagedTransport());
