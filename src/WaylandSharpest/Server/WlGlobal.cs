namespace Wayland.Server;

/// <summary>
/// A published server global (<c>wl_global</c>). When a client binds it, the
/// registered <see cref="BindHandler"/> runs and is expected to construct the
/// matching <see cref="WlResource"/> subclass with the supplied id.
/// </summary>
public sealed class WlGlobal : IDisposable
{
    /// <summary>Invoked when a client binds the global.</summary>
    public delegate void BindHandler(WlClient client, uint version, uint id);

    private readonly BindHandler _onBind;
    private readonly IWlGlobal _impl;
    private readonly WlServerDisplay _display;
    private Action? _withdrawn;
    private bool _removed;
    private bool _disposed;

    internal WlGlobal(WlServerDisplay display, WlInterfaceSpec iface, int version, BindHandler onBind)
    {
        _onBind = onBind;
        _display = display;
        InterfaceName = iface.Name;
        _impl = display.Impl.CreateGlobal(this, iface, version);
    }

    /// <summary>The protocol interface this global advertises.</summary>
    public string InterfaceName { get; }

    /// <summary>The interface version this global was published at.</summary>
    public uint Version => _impl.Version;

    /// <summary>
    /// The <c>wl_registry</c> name this global is advertised under to
    /// <paramref name="client"/>, or 0 when that client cannot see it — a filter
    /// installed with <see cref="WlServerDisplay.SetGlobalFilter"/> may hide it.
    /// Protocols that hand a client a global to bind by name need this.
    /// </summary>
    public uint NameFor(WlClient client) => _impl.NameFor(client);

    /// <summary>True once <see cref="Remove"/> has been called.</summary>
    public bool IsRemoved => _removed;

    /// <summary>
    /// Unpublishes the global and notifies clients with
    /// <c>wl_registry.global_remove</c>, without destroying it. Requests that
    /// are already in flight still resolve, which is what makes this safe where
    /// <see cref="Dispose"/> is not: a client that sent <c>wl_registry.bind</c>
    /// before processing the removal would otherwise be killed with a protocol
    /// error. Call <see cref="Dispose"/> once <see cref="Withdrawn"/> has been
    /// raised, or after at least one client round-trip.
    /// </summary>
    /// <exception cref="InvalidOperationException">The global has already been removed.</exception>
    public void Remove()
    {
        _impl.Remove();
        _removed = true;
    }

    /// <summary>
    /// Raised when no client can still bind this global and it is safe to
    /// dispose. Requires libwayland 1.26; see
    /// <see cref="SupportsWithdrawnNotification"/>. Runs on the dispatch thread
    /// inside libwayland — keep it short.
    /// </summary>
    public event Action? Withdrawn
    {
        add
        {
            _withdrawn += value;
            _impl.WithdrawnHandler = RaiseWithdrawn;
        }

        remove => _withdrawn -= value;
    }

    private void RaiseWithdrawn() => _withdrawn?.Invoke();

    /// <summary>
    /// Whether the loaded libwayland can report <see cref="Withdrawn"/> (1.26+).
    /// When false, <see cref="RemoveAndDispose"/> falls back to a timed grace
    /// period.
    /// </summary>
    public bool SupportsWithdrawnNotification => _impl.SupportsWithdrawn;

    /// <summary>
    /// Removes the global and disposes it once no client can still bind it — the
    /// safe counterpart to <see cref="Dispose"/> for a global clients may be
    /// racing, such as a <c>wl_output</c> for a monitor being unplugged. Returns
    /// immediately; disposal happens later on the display's event loop. Uses the
    /// withdrawn notification where libwayland 1.26 is present and a
    /// <paramref name="graceMs"/> timer otherwise.
    /// </summary>
    /// <param name="graceMs">Fallback delay before disposal, in milliseconds.</param>
    /// <exception cref="InvalidOperationException">The global has already been removed.</exception>
    public void RemoveAndDispose(int graceMs = 5000)
    {
        if (SupportsWithdrawnNotification)
        {
            Withdrawn += Dispose;
            Remove();
            return;
        }

        Remove();

        // One-shot: the source removes itself before disposing, so a disposal
        // that throws cannot leave the timer armed.
        WlEventSource? timer = null;
        timer = _display.EventLoop.AddTimer(() =>
        {
            timer!.Remove();
            Dispose();
        });
        timer.UpdateTimer(graceMs <= 0 ? 1 : graceMs);
    }

    /// <summary>Called by the transport when a client binds this global.</summary>
    internal void HandleBind(WlClient client, uint version, uint id) => _onBind(client, version, id);

    /// <summary>
    /// Destroys the global immediately. A client with a <c>bind</c> already in
    /// flight will be killed with a protocol error; use
    /// <see cref="RemoveAndDispose"/> where that is possible.
    /// </summary>
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
