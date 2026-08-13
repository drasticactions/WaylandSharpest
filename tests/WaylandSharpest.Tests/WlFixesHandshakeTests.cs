using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// The wl_fixes handshake, which is what lets a removed global report itself
/// withdrawn: a client acknowledges the removal, and the registry stops
/// treating the global as bindable for it.
/// </summary>
[LibWaylandOnly("The handshake is serviced by WlFixesSupport and WlForeignResource, which both work on libwayland's own registry state.")]
public sealed class WlFixesHandshakeTests : LoopbackHarness
{
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
