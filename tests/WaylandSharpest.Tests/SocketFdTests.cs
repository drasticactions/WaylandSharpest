using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Serving on an inherited listening socket, as under systemd socket
/// activation: the test plays the role of the activator by creating the socket
/// itself and handing the fd over.
/// </summary>
public sealed class SocketFdTests : IDisposable
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;
    private const int SocketPathLength = 108;

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int bind(int fd, SockAddrUn* addr, uint len);

    [DllImport("libc", SetLastError = true)]
    private static extern int listen(int fd, int backlog);

    [DllImport("libc")]
    private static extern int close(int fd);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SockAddrUn
    {
        public ushort Family;
        public fixed byte Path[SocketPathLength];
    }

    private readonly WlServerDisplay _server = WlServerDisplay.Create();
    private readonly string _socketPath =
        Path.Combine(Path.GetTempPath(), $"waylandsharpest-{Environment.ProcessId}-{Guid.NewGuid():N}.sock");

    public void Dispose()
    {
        _server.Dispose();
        File.Delete(_socketPath);
    }

    private unsafe int CreateListeningSocket()
    {
        var fd = socket(AF_UNIX, SOCK_STREAM, 0);
        Assert.True(fd >= 0, $"socket() failed: {Marshal.GetLastWin32Error()}");

        var addr = default(SockAddrUn);
        addr.Family = AF_UNIX;
        var bytes = System.Text.Encoding.UTF8.GetBytes(_socketPath);
        Assert.True(bytes.Length < SocketPathLength);
        for (var i = 0; i < bytes.Length; i++)
        {
            addr.Path[i] = bytes[i];
        }

        Assert.Equal(0, bind(fd, &addr, (uint)sizeof(SockAddrUn)));
        Assert.Equal(0, listen(fd, 8));
        return fd;
    }

    [Fact]
    public void Adopted_listening_socket_accepts_a_client()
    {
        // The display takes ownership of the fd; the test must not close it.
        _server.AddSocketFd(CreateListeningSocket());

        using var global = _server.CreateGlobal(WlCompositor.Interface, 6, static (_, _, _) => { });

        // An absolute name bypasses $XDG_RUNTIME_DIR, so the socket can live in
        // the test's temp directory.
        using var client = WlDisplay.Connect(_socketPath);
        using var registry = client.GetRegistry();
        var announced = new List<string>();
        registry.Global += (_, e) => announced.Add(e.Interface);

        client.Flush();

        // The first dispatch accepts the connection; the client's requests are
        // only readable on a later one. Bounded so a failure is a failure, not a
        // hang.
        for (var i = 0; i < 10 && _server.Clients.Count == 0; i++)
        {
            _server.EventLoop.Dispatch(100);
        }

        Assert.Single(_server.Clients);
        _server.EventLoop.Dispatch(100);
        _server.FlushClients();
        client.Dispatch();

        Assert.Contains("wl_compositor", announced);
    }

    [Fact]
    public void Adopting_an_invalid_fd_throws()
    {
        Assert.Throws<WaylandException>(() => _server.AddSocketFd(-1));
    }
}
