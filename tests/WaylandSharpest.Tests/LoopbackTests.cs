using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Full-stack tests: an in-process libwayland server and client connected over a
/// socketpair, exercising the generated core-protocol bindings on both sides.
/// </summary>
public sealed class LoopbackTests : LoopbackHarness
{
    [Fact]
    public void Registry_advertises_globals()
    {
        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });

        using var registry = Client.GetRegistry();
        var announced = new List<(uint Name, string Interface, uint Version)>();
        registry.Global += (_, e) => announced.Add((e.Name, e.Interface, e.Version));

        PumpToClient();

        var compositor = Assert.Single(announced, g => g.Interface == "wl_compositor");
        Assert.Equal(6u, compositor.Version);
    }

    [Fact]
    public void Bind_and_create_surface_roundtrip()
    {
        WlCompositorResource? boundCompositor = null;
        WlSurfaceResource? serverSurface = null;
        var committed = false;

        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            boundCompositor = new WlCompositorResource(client, version, id);
            boundCompositor.CreateSurface += (sender, e) =>
            {
                serverSurface = new WlSurfaceResource(boundCompositor.Client, boundCompositor.Version, e.Id);
                serverSurface.Commit += (_, _) => committed = true;
            };
        });

        using var registry = Client.GetRegistry();
        uint compositorName = 0;
        registry.Global += (_, e) =>
        {
            if (e.Interface == "wl_compositor")
            {
                compositorName = e.Name;
            }
        };
        PumpToClient();
        Assert.NotEqual(0u, compositorName);

        using var compositor = registry.Bind<WlCompositor>(compositorName, 6);
        PumpToServer();
        Assert.NotNull(boundCompositor);
        Assert.Equal(6u, boundCompositor!.Version);

        var surface = compositor.CreateSurface();
        surface.Attach(null, 0, 0);
        surface.Commit();
        PumpToServer();

        Assert.NotNull(serverSurface);
        Assert.True(committed);

        // Destructor request: client destroys, server observes resource death.
        var destroyed = false;
        serverSurface!.Destroyed += (_, _) => destroyed = true;
        surface.Dispose();
        Assert.True(surface.IsDestroyed);
        PumpToServer();
        Assert.True(destroyed);
        Assert.True(serverSurface.IsDestroyed);
    }

    [Fact]
    public void Server_events_reachClient()
    {
        WlSeatResource? serverSeat = null;
        using var global = Server.CreateGlobal(WlSeat.Interface, 5, (client, version, id) =>
        {
            serverSeat = new WlSeatResource(client, version, id);
        });

        using var registry = Client.GetRegistry();
        uint seatName = 0;
        registry.Global += (_, e) =>
        {
            if (e.Interface == "wl_seat")
            {
                seatName = e.Name;
            }
        };
        PumpToClient();

        using var seat = registry.Bind<WlSeat>(seatName, 5);
        var received = new List<(WlSeat.Capability Caps, string? Name)>();
        seat.Capabilities += (_, e) => received.Add((e.Capabilities, null));
        seat.Name += (_, e) => received.Add((default, e.Name));

        PumpToServer();
        Assert.NotNull(serverSeat);
        serverSeat!.SendCapabilities(WlSeat.Capability.Pointer | WlSeat.Capability.Keyboard);
        serverSeat.SendName("seat0");
        Server.FlushClients();
        Client.Dispatch();

        Assert.Equal(2, received.Count);
        Assert.Equal(WlSeat.Capability.Pointer | WlSeat.Capability.Keyboard, received[0].Caps);
        Assert.Equal("seat0", received[1].Name);
    }

    [Fact]
    public void Event_handler_exceptions_surface_on_dispatch()
    {
        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });

        using var registry = Client.GetRegistry();
        registry.Global += (_, _) => throw new InvalidOperationException("boom");

        Client.Flush();
        Server.EventLoop.Dispatch(100);
        Server.FlushClients();

        var ex = Assert.Throws<WaylandException>(() => Client.Dispatch());
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}
