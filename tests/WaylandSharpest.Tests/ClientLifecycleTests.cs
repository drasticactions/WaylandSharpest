using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Tests for observing clients as they come and go, which is what lets a
/// compositor create per-client state at connect time rather than lazily at
/// first bind.
/// </summary>
public sealed class ClientLifecycleTests : IDisposable
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int socketpair(int domain, int type, int protocol, int* sv);

    private readonly WlServerDisplay _server = WlServerDisplay.Create();
    private readonly List<WlDisplay> _clients = [];

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _server.Dispose();
    }

    /// <summary>Connects a client through <c>wl_client_create</c>, as a listening socket would.</summary>
    private WlDisplay Connect()
    {
        int fd0, fd1;
        unsafe
        {
            var fds = stackalloc int[2];
            Assert.Equal(0, socketpair(AF_UNIX, SOCK_STREAM, 0, fds));
            fd0 = fds[0];
            fd1 = fds[1];
        }

        _server.CreateClient(fd0);
        var client = WlDisplay.ConnectToFd(fd1);
        _clients.Add(client);
        return client;
    }

    [Fact]
    public void Client_created_event_fires_before_first_request()
    {
        var created = new List<WlClient>();
        _server.ClientCreated += created.Add;

        Connect();

        var client = Assert.Single(created);
        Assert.False(client.IsDestroyed);
        Assert.Same(client, Assert.Single(_server.Clients));
    }

    [Fact]
    public void Clients_lists_every_connection_and_interns_wrappers()
    {
        var created = new List<WlClient>();
        _server.ClientCreated += created.Add;

        Connect();
        Connect();

        Assert.Equal(2, created.Count);
        Assert.Equal(2, _server.Clients.Count);

        // Identity matches what the connect handler saw, both times.
        Assert.Equal(created.ToHashSet(), _server.Clients.ToHashSet());
    }

    [Fact]
    public void Clients_shrinks_on_disconnect()
    {
        var client = Connect();
        Assert.Single(_server.Clients);

        client.Dispose();
        _clients.Remove(client);
        _server.EventLoop.Dispatch(100);

        Assert.Empty(_server.Clients);
    }

    [Fact]
    public void Connection_can_be_rejected_from_the_created_handler()
    {
        WlClient? seen = null;
        _server.ClientCreated += client =>
        {
            seen = client;
            client.Destroy();
        };

        Connect();

        Assert.NotNull(seen);
        Assert.True(seen!.IsDestroyed);
        Assert.Empty(_server.Clients);
    }

    [Fact]
    public void Created_handler_exceptions_surface_on_dispatch()
    {
        _server.ClientCreated += _ => throw new InvalidOperationException("connect boom");

        Connect();

        var ex = Assert.Throws<WaylandException>(() => _server.EventLoop.Dispatch(0));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void Clients_is_empty_before_anyone_connects()
    {
        Assert.Empty(_server.Clients);
    }
}
