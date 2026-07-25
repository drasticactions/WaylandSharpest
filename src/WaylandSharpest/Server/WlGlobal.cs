using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// A published server global (<c>wl_global</c>). When a client binds it, the
/// registered <see cref="BindHandler"/> runs and is expected to construct the
/// matching <see cref="WlResource"/> subclass with the supplied id.
/// </summary>
public sealed unsafe class WlGlobal : IDisposable
{
    /// <summary>Invoked when a client binds the global.</summary>
    public delegate void BindHandler(WlClient client, uint version, uint id);

    private readonly WlServerDisplay _display;
    private readonly BindHandler _onBind;
    private nint _handle;
    private GCHandle _selfHandle;

    internal WlGlobal(WlServerDisplay display, WlInterfaceSpec iface, int version, BindHandler onBind)
    {
        _display = display;
        _onBind = onBind;
        _selfHandle = GCHandle.Alloc(this);
        var global = LibWaylandServer.wl_global_create(
            (wl_display*)display.RawHandle,
            iface.NativePointer,
            version,
            (void*)GCHandle.ToIntPtr(_selfHandle),
            &BindThunk);
        if (global == null)
        {
            _selfHandle.Free();
            throw new WaylandException($"wl_global_create failed for '{iface.Name}' v{version}.");
        }

        _handle = (nint)global;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BindThunk(wl_client* client, void* data, uint version, uint id)
    {
        var global = (WlGlobal?)GCHandle.FromIntPtr((nint)data).Target;
        if (global is null)
        {
            return;
        }

        try
        {
            global._onBind(WlClient.Get((nint)client, global._display), version, id);
        }
        catch (Exception ex)
        {
            global._display.CaptureDispatchException(ex);
        }
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        LibWaylandServer.wl_global_destroy((wl_global*)_handle);
        _handle = 0;
        _selfHandle.Free();
    }
}
