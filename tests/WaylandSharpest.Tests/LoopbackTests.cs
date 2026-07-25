using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Full-stack tests: an in-process libwayland server and client connected over a
/// socketpair, exercising the generated core-protocol bindings on both sides.
/// </summary>
public sealed class LoopbackTests : IDisposable
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int socketpair(int domain, int type, int protocol, int* sv);

    private readonly WlServerDisplay _server;
    private readonly WlDisplay _client;

    public LoopbackTests()
    {
        _server = WlServerDisplay.Create();
        int fd0, fd1;
        unsafe
        {
            var fds = stackalloc int[2];
            Assert.Equal(0, socketpair(AF_UNIX, SOCK_STREAM, 0, fds));
            fd0 = fds[0];
            fd1 = fds[1];
        }

        _server.CreateClient(fd0);
        _client = WlDisplay.ConnectToFd(fd1);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    /// <summary>Client flushes, server processes, server flushes, client dispatches.</summary>
    private void PumpToClient()
    {
        _client.Flush();
        _server.EventLoop.Dispatch(100);
        _server.FlushClients();
        _client.Dispatch();
    }

    /// <summary>Client flushes and the server processes the requests.</summary>
    private void PumpToServer()
    {
        _client.Flush();
        _server.EventLoop.Dispatch(100);
    }

    [Fact]
    public void Registry_advertises_globals()
    {
        using var global = _server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });

        using var registry = _client.GetRegistry();
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

        using var global = _server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            boundCompositor = new WlCompositorResource(client, version, id);
            boundCompositor.CreateSurface += (sender, e) =>
            {
                serverSurface = new WlSurfaceResource(boundCompositor.Client, boundCompositor.Version, e.Id);
                serverSurface.Commit += (_, _) => committed = true;
            };
        });

        using var registry = _client.GetRegistry();
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
    public void Server_events_reach_client()
    {
        WlSeatResource? serverSeat = null;
        using var global = _server.CreateGlobal(WlSeat.Interface, 5, (client, version, id) =>
        {
            serverSeat = new WlSeatResource(client, version, id);
        });

        using var registry = _client.GetRegistry();
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
        _server.FlushClients();
        _client.Dispatch();

        Assert.Equal(2, received.Count);
        Assert.Equal(WlSeat.Capability.Pointer | WlSeat.Capability.Keyboard, received[0].Caps);
        Assert.Equal("seat0", received[1].Name);
    }

    [Fact]
    public void Event_handler_exceptions_surface_on_dispatch()
    {
        using var global = _server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });

        using var registry = _client.GetRegistry();
        registry.Global += (_, _) => throw new InvalidOperationException("boom");

        _client.Flush();
        _server.EventLoop.Dispatch(100);
        _server.FlushClients();

        var ex = Assert.Throws<WaylandException>(() => _client.Dispatch());
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}
