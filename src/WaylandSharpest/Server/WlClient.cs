namespace Wayland.Server;

/// <summary>
/// A connected client on the server side (<c>wl_client</c>). Instances are
/// interned per underlying transport client so resource callbacks observe
/// stable identity, and are invalidated when the client disconnects.
/// </summary>
public sealed class WlClient
{
    private readonly IWlClient _impl;
    private bool _destroyed;

    internal readonly Dictionary<nint, WlResource> Owned = [];

    internal WlClient(IWlClient impl, WlServerDisplay? display)
    {
        _impl = impl;
        Display = display;
    }

    public nint RawHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            return _impl.RawHandle;
        }
    }

    /// <summary>The owning display, when known (clients created through WaylandSharpest APIs).</summary>
    public WlServerDisplay? Display { get; }

    /// <summary>True once the client has disconnected or been destroyed.</summary>
    public bool IsDestroyed => _destroyed;

    /// <summary>Raised when the client disconnects and its state is torn down.</summary>
    public event Action? Destroyed;

    internal IWlClient Impl
    {
        get
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            return _impl;
        }
    }

    /// <summary>
    /// The client's process and user identity. Valid for the life of the
    /// connection; a pid can be reused after the peer exits, so do not treat it
    /// as a durable handle.
    /// </summary>
    public WlClientCredentials Credentials => Impl.GetCredentials();

    /// <summary>
    /// The peer's identity, or false for a client with no local process behind
    /// it, such as one a remote channel carries.
    /// </summary>
    public bool TryGetCredentials(out WlClientCredentials credentials)
    {
        try
        {
            credentials = Impl.GetCredentials();
            return true;
        }
        catch (NotSupportedException)
        {
            credentials = default;
            return false;
        }
    }

    /// <summary>The client's connection file descriptor. The client owns it; do not close it.</summary>
    public int Fd => Impl.Fd;

    /// <summary>
    /// The token table this client's fd-slot values are minted from, or null
    /// when they are kernel file descriptors. Use it to resolve a slot that
    /// arrived in a request.
    /// </summary>
    public IFdSlotTable? FdSlots => Impl.FdSlots;

    /// <summary>
    /// The client's protocol object with the given id, or <c>null</c> if it has
    /// none. Returns objects created by this library as their
    /// <see cref="WlResource"/> wrapper; use <see cref="GetObjectHandle"/> for
    /// objects owned by another implementation.
    /// </summary>
    public WlResource? GetObject(uint id)
    {
        var handle = GetObjectHandle(id);
        return handle == 0 ? null : Owned.GetValueOrDefault(handle);
    }

    /// <summary>Raw <c>wl_resource*</c> for the client's object <paramref name="id"/>, or 0.</summary>
    public nint GetObjectHandle(uint id) => Impl.GetObjectHandle(id);

    /// <summary>
    /// Releases an fd-slot value this client delivered in a request, or that
    /// was staged for an event and never sent.
    /// </summary>
    public void CloseFd(int fd) => _impl.CloseFd(fd);

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_destroyed, this);
        _impl.Flush();
    }

    /// <summary>Forcibly disconnects the client.</summary>
    public void Destroy()
    {
        if (!_destroyed)
        {
            _impl.Destroy();
        }
    }

    internal void OnTransportDestroyed()
    {
        _destroyed = true;
        Destroyed?.Invoke();
    }
}
