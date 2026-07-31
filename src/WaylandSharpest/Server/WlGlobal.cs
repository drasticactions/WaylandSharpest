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

    internal WlGlobal(WlServerDisplay display, WlInterfaceSpec iface, int version, BindHandler onBind)
    {
        _onBind = onBind;
        _impl = display.Impl.CreateGlobal(this, iface, version);
    }

    /// <summary>Called by the transport when a client binds this global.</summary>
    internal void HandleBind(WlClient client, uint version, uint id) => _onBind(client, version, id);

    public void Dispose() => _impl.Dispose();
}
