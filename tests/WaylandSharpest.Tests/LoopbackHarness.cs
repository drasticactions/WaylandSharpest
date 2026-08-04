using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// An in-process server and client joined by a socketpair and pumped by hand.
/// Nothing here blocks indefinitely: dispatch timeouts are bounded so a missing
/// message fails the test rather than hanging the run.
/// </summary>
public abstract class LoopbackHarness : IDisposable
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int socketpair(int domain, int type, int protocol, int* sv);

    protected LoopbackHarness()
    {
        Server = WlServerDisplay.Create();
        int fd0, fd1;
        unsafe
        {
            var fds = stackalloc int[2];
            Assert.Equal(0, socketpair(AF_UNIX, SOCK_STREAM, 0, fds));
            fd0 = fds[0];
            fd1 = fds[1];
        }

        ServerClient = Server.CreateClient(fd0);
        Client = WlDisplay.ConnectToFd(fd1);
    }

    protected WlServerDisplay Server { get; }

    /// <summary>The server's view of the single connected client.</summary>
    protected WlClient ServerClient { get; }

    protected WlDisplay Client { get; }

    public virtual void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Client flushes, server processes, server flushes, client dispatches.</summary>
    protected void PumpToClient()
    {
        Client.Flush();
        Server.EventLoop.Dispatch(100);
        Server.FlushClients();
        Client.Dispatch();
    }

    /// <summary>Client flushes and the server processes the requests.</summary>
    protected void PumpToServer()
    {
        Client.Flush();
        Server.EventLoop.Dispatch(100);
    }

    /// <summary>Server flushes queued events and the client dispatches them.</summary>
    protected void PumpEventsToClient()
    {
        Server.FlushClients();
        Client.Dispatch();
    }

    /// <summary>
    /// Pumps both directions without blocking when nothing is on the way. Use
    /// where the point of the test is that an event may legitimately not arrive;
    /// <see cref="PumpToClient"/> would hang there.
    /// </summary>
    protected void TryPump()
    {
        Client.Flush();
        Server.EventLoop.Dispatch(100);
        Server.FlushClients();
        if (Client.TryReadEvents(100))
        {
            Client.DispatchPending();
        }
    }

    /// <summary>Binds the single global advertising <paramref name="interfaceName"/> at <paramref name="version"/>.</summary>
    protected T Bind<T>(string interfaceName, uint version)
        where T : WlProxy, IWaylandObject<T>
    {
        using var registry = Client.GetRegistry();
        uint name = 0;
        registry.Global += (_, e) =>
        {
            if (e.Interface == interfaceName)
            {
                name = e.Name;
            }
        };
        PumpToClient();
        Assert.NotEqual(0u, name);
        return registry.Bind<T>(name, version);
    }
}
