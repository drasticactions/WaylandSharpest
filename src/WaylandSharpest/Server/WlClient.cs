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

    internal IWlClient Impl
    {
        get
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            return _impl;
        }
    }

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

    internal void OnTransportDestroyed() => _destroyed = true;
}
