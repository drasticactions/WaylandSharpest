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
    public void Withdrawn_notification_fires_once_no_registry_still_offers_the_global()
    {
        var global = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        if (!global.SupportsWithdrawnNotification)
        {
            Assert.Throws<WaylandException>(() => global.Withdrawn += static () => { });
            global.Dispose();
            Assert.Skip("libwayland-server is older than 1.26 and does not export wl_global_set_withdrawn_listener.");
        }

        // libwayland implements only the ack half of wl_fixes; destroy_registry
        // is the compositor's job, and its argument is always a foreign
        // resource because libwayland owns wl_registry objects.
        using var fixesGlobal = Server.CreateGlobal(WlFixes.Interface, 2, static (client, version, id) =>
        {
            var resource = new WlFixesResource(client, version, id);
            resource.DestroyRegistry += (_, e) => WlForeignResource.Destroy(e.RegistryHandle);
        });

        var withdrawn = 0;
        global.Withdrawn += () => withdrawn++;

        var registry = Client.GetRegistry();
        var announced = new Dictionary<string, uint>();
        registry.Global += (_, e) => announced[e.Interface] = e.Name;
        PumpToClient();
        using var fixes = registry.Bind<WlFixes>(announced["wl_fixes"], 2);

        // A registry that was offered the global holds it back until the client
        // acknowledges or the registry goes away, which is the whole point: a
        // bind may still be in flight.
        global.Remove();
        PumpToClient();
        Assert.Equal(0, withdrawn);

        // wl_registry has no destructor request of its own -- that gap is why
        // wl_fixes.destroy_registry exists -- so disposing the client proxy
        // alone would leave the server-side registry, and the offer, in place.
        fixes.DestroyRegistry(registry);
        registry.Dispose();
        PumpToServer();

        Assert.Equal(1, withdrawn);
        global.Dispose();
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

    [Fact]
    public void Fixes_acknowledgement_completes_the_removal_handshake()
    {
        // libwayland does not publish wl_fixes itself; a compositor that wants
        // it creates the global and routes the request through.
        WlFixesResource? serverFixes = null;
        using var fixesGlobal = Server.CreateGlobal(WlFixes.Interface, 2, (client, version, id) =>
            serverFixes = new WlFixesResource(client, version, id));
        var going = Server.CreateGlobal(WlSeat.Interface, 5, static (_, _, _) => { });

        using var registry = Client.GetRegistry();
        var announced = new Dictionary<string, uint>();
        registry.Global += (_, e) => announced[e.Interface] = e.Name;
        PumpToClient();

        using var fixes = registry.Bind<WlFixes>(announced["wl_fixes"], 2);
        PumpToServer();
        Assert.NotNull(serverFixes);

        var registryHandle = ServerClient.GetObjectHandle(registry.Id);
        Assert.NotEqual(0, registryHandle);
        var seatName = announced["wl_seat"];

        if (!WlFixesSupport.IsSupported)
        {
            Assert.Throws<WaylandException>(() =>
                WlFixesSupport.HandleAckGlobalRemove(serverFixes!, registryHandle, seatName));
            going.Dispose();
            Assert.Skip("libwayland-server is older than 1.26 and does not export wl_fixes_handle_ack_global_remove.");
        }

        var withdrawn = 0;
        going.Withdrawn += () => withdrawn++;
        going.Remove();
        PumpToClient();
        Assert.Equal(0, withdrawn);

        // Acknowledging is what tells the server no further bind can arrive.
        WlFixesSupport.HandleAckGlobalRemove(serverFixes!, registryHandle, seatName);
        Assert.Equal(1, withdrawn);

        going.Dispose();
        TryPump();
    }

    [Fact]
    public void Acknowledging_a_global_that_was_not_removed_is_a_protocol_error()
    {
        WlFixesResource? serverFixes = null;
        using var fixesGlobal = Server.CreateGlobal(WlFixes.Interface, 2, (client, version, id) =>
            serverFixes = new WlFixesResource(client, version, id));

        using var registry = Client.GetRegistry();
        var announced = new Dictionary<string, uint>();
        registry.Global += (_, e) => announced[e.Interface] = e.Name;
        PumpToClient();

        using var fixes = registry.Bind<WlFixes>(announced["wl_fixes"], 2);
        PumpToServer();

        if (!WlFixesSupport.IsSupported)
        {
            Assert.Skip("libwayland-server is older than 1.26 and does not export wl_fixes_handle_ack_global_remove.");
        }

        // wl_fixes is still published, so acknowledging its removal is
        // invalid_ack_remove and libwayland kills the client.
        WlFixesSupport.HandleAckGlobalRemove(
            serverFixes!, ServerClient.GetObjectHandle(registry.Id), announced["wl_fixes"]);

        Server.FlushClients();
        Assert.Throws<WaylandProtocolException>(TryPump);
    }
}
