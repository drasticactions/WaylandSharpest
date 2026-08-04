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
public sealed class GlobalRemovalTests : LoopbackHarness
{
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

        using var registry = Client.GetRegistry();
        var removed = new List<uint>();
        registry.GlobalRemove += (_, e) => removed.Add(e.Name);
        PumpToClient();

        global.RemoveAndDispose(graceMs: 1);
        Assert.True(global.IsRemoved);

        // Still alive right after the call; disposal is deferred to the loop.
        PumpToClient();
        Assert.Single(removed);

        // The fallback timer (or the withdrawn notification) fires on a later
        // dispatch, and disposal makes the global reject further removal.
        for (var i = 0; i < 10; i++)
        {
            Server.EventLoop.Dispatch(20);
        }

        Assert.Throws<ObjectDisposedException>(() => global.Remove());
    }

    [Fact]
    public void Withdrawn_notification_reports_when_disposal_is_safe()
    {
        var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        if (!global.SupportsWithdrawnNotification)
        {
            Assert.Throws<WaylandException>(() => global.Withdrawn += static () => { });
            global.Dispose();
            Assert.Skip("libwayland-server is older than 1.26 and does not export wl_global_set_withdrawn_listener.");
        }

        var withdrawn = 0;
        global.Withdrawn += () => withdrawn++;

        using var registry = Client.GetRegistry();
        PumpToClient();

        global.Remove();
        PumpToClient();
        for (var i = 0; i < 10 && withdrawn == 0; i++)
        {
            Server.EventLoop.Dispatch(20);
        }

        Assert.Equal(1, withdrawn);
        global.Dispose();
    }

    [Fact]
    public void Fixes_acknowledgement_is_serviced_by_libwayland()
    {
        // libwayland does not publish wl_fixes itself; a compositor that wants
        // it creates the global and routes the request through.
        WlFixesResource? serverFixes = null;
        using var fixesGlobal = Server.CreateGlobal(WlFixes.Interface, 1, (client, version, id) =>
            serverFixes = new WlFixesResource(client, version, id));

        using var registry = Client.GetRegistry();
        var announced = new Dictionary<string, uint>();
        registry.Global += (_, e) => announced[e.Interface] = e.Name;
        PumpToClient();

        using var fixes = registry.Bind<WlFixes>(announced["wl_fixes"], 1);
        PumpToServer();
        Assert.NotNull(serverFixes);

        var registryHandle = ServerClient.GetObjectHandle(registry.Id);
        Assert.NotEqual(0, registryHandle);

        if (!WlFixesSupport.IsSupported)
        {
            Assert.Throws<WaylandException>(() =>
                WlFixesSupport.HandleAckGlobalRemove(serverFixes!, registryHandle, announced["wl_fixes"]));
            Assert.Skip("libwayland-server is older than 1.26 and does not export wl_fixes_handle_ack_global_remove.");
        }

        WlFixesSupport.HandleAckGlobalRemove(serverFixes!, registryHandle, announced["wl_fixes"]);
        PumpToClient();
    }
}
