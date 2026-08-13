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
        Transport = transport;
        _impl = transport.CreateDisplay(this);
        EventLoop = new WlEventLoop(_impl.EventLoop, this);
    }

    /// <summary>The transport this display runs on.</summary>
    public IWlServerTransport Transport { get; }

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

    public bool SupportsLocalSocket => _impl.SupportsLocalSocket;

    /// <summary>Adds a socket with an automatically chosen name and returns it.</summary>
    public string AddSocketAuto() => _impl.AddSocketAuto();

    /// <summary>Adds a socket with an explicit name under <c>$XDG_RUNTIME_DIR</c>.</summary>
    public void AddSocket(string name) => _impl.AddSocket(name);

    /// <summary>
    /// Serves on an already-listening socket, taking ownership of
    /// <paramref name="fd"/>. Use for systemd socket activation, where the
    /// listening socket is inherited rather than created.
    /// </summary>
    public void AddSocketFd(int fd) => _impl.AddSocketFd(fd);

    /// <summary>Creates a client for an already-connected socket fd.</summary>
    public WlClient CreateClient(int fd) => _impl.CreateClient(fd);

    /// <summary>
    /// Creates a client served by <paramref name="transport"/>, taking
    /// ownership of it. Use for clients that arrive over something other than a
    /// connection file descriptor.
    /// </summary>
    public WlClient CreateClient(IWlClientTransport transport) => _impl.CreateClient(transport);

    /// <summary>
    /// Raised when a client connects, before it has sent any request. Destroy
    /// the client from the handler to reject the connection.
    /// </summary>
    public event Action<WlClient>? ClientCreated
    {
        add
        {
            _clientCreated += value;
            _impl.ClientCreatedHandler = RaiseClientCreated;
        }

        remove => _clientCreated -= value;
    }

    private Action<WlClient>? _clientCreated;

    private void RaiseClientCreated(WlClient client) => _clientCreated?.Invoke(client);

    /// <summary>The currently connected clients, in connection order. A snapshot, not a live view.</summary>
    public IReadOnlyList<WlClient> Clients => _impl.GetClients();

    /// <summary>
    /// Decides whether <paramref name="client"/> may see a global.
    /// <paramref name="global"/> is the owning wrapper when the global was
    /// created through this display; <paramref name="interfaceName"/> is
    /// always present.
    /// </summary>
    public delegate bool GlobalFilter(WlClient client, WlGlobal? global, string interfaceName);

    /// <summary>
    /// Installs (or clears, with null) a per-client global visibility filter:
    /// globals for which the filter returns false are invisible to that
    /// client, both in registry listings and for binding.
    /// </summary>
    public void SetGlobalFilter(GlobalFilter? filter) => _impl.SetGlobalFilter(filter);

    /// <summary>
    /// Logs every protocol message crossing this display — the structured form
    /// of <c>WAYLAND_DEBUG=1</c>. The callback runs inline on the dispatch
    /// thread, so keep it cheap and do not call back into the protocol from it.
    /// Dispose the returned registration to stop logging.
    /// </summary>
    public IDisposable AddProtocolLogger(WlProtocolLogger logger) => _impl.AddProtocolLogger(logger);

    /// <summary>
    /// Whether this display services <c>wl_fixes</c>. The managed transport
    /// always does. The libwayland transport does from 1.26, which is where
    /// <c>wl_fixes_handle_ack_global_remove</c> arrives.
    /// </summary>
    public bool SupportsFixes => _impl.SupportsFixes;

    /// <summary>
    /// Services a client's <c>wl_fixes.ack_global_remove</c> request, on
    /// whichever transport this display runs.
    /// </summary>
    public void AckGlobalRemove(WlClient client, nint fixesHandle, nint registryHandle, uint globalName) =>
        _impl.AckGlobalRemove(client, fixesHandle, registryHandle, globalName);

    /// <summary>
    /// Services a client's <c>wl_fixes.destroy_registry</c> request, on
    /// whichever transport this display runs.
    /// </summary>
    public void DestroyRegistry(WlClient client, nint registryHandle) =>
        _impl.DestroyRegistry(client, registryHandle);

    /// <summary>Publishes a global; <paramref name="onBind"/> is invoked when a client binds it.</summary>
    public WlGlobal CreateGlobal(WlInterfaceSpec iface, int version, WlGlobal.BindHandler onBind) =>
        new(this, iface, version, onBind);

    /// <summary>Runs the event loop until <see cref="Terminate"/> is called.</summary>
    public void Run() => _impl.Run();

    public void Terminate() => _impl.Terminate();

    public void FlushClients() => _impl.FlushClients();

    public uint NextSerial() => _impl.NextSerial();

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
