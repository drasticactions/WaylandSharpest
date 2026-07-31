namespace Wayland.Server;

/// <summary>
/// Server-side <c>wl_display</c>: the root object of a compositor, owning the
/// listening sockets, event loop, clients, and globals.
/// </summary>
public sealed class WlServerDisplay : IDisposable
{
    private readonly IWlDisplay _impl;
    private Exception? _dispatchException;
    private bool _disposed;

    private WlServerDisplay(IWlServerTransport transport)
    {
        _impl = transport.CreateDisplay(this);
        EventLoop = new WlEventLoop(_impl.EventLoop, this);
    }

    /// <summary>Creates a display on the default libwayland transport.</summary>
    public static WlServerDisplay Create() => Create(LibWaylandTransport.Instance);

    /// <summary>Creates a display on an explicit transport.</summary>
    public static WlServerDisplay Create(IWlServerTransport transport) => new(transport);

    public nint RawHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _impl.RawHandle;
        }
    }

    internal IWlDisplay Impl => _impl;

    public WlEventLoop EventLoop { get; }

    /// <summary>Adds a socket with an automatically chosen name and returns it.</summary>
    public string AddSocketAuto() => _impl.AddSocketAuto();

    /// <summary>Adds a socket with an explicit name under <c>$XDG_RUNTIME_DIR</c>.</summary>
    public void AddSocket(string name) => _impl.AddSocket(name);

    /// <summary>Creates a client for an already-connected socket fd.</summary>
    public WlClient CreateClient(int fd) => _impl.CreateClient(fd);

    /// <summary>Publishes a global; <paramref name="onBind"/> is invoked when a client binds it.</summary>
    public WlGlobal CreateGlobal(WlInterfaceSpec iface, int version, WlGlobal.BindHandler onBind) =>
        new(this, iface, version, onBind);

    /// <summary>Runs the event loop until <see cref="Terminate"/> is called.</summary>
    public void Run() => _impl.Run();

    public void Terminate() => _impl.Terminate();

    public void FlushClients() => _impl.FlushClients();

    internal void CaptureDispatchException(Exception exception) => _dispatchException ??= exception;

    internal void RethrowPendingDispatchException()
    {
        if (_dispatchException is { } pending)
        {
            _dispatchException = null;
            throw new WaylandException("A request handler threw during dispatch.", pending);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _impl.Dispose();
    }
}
