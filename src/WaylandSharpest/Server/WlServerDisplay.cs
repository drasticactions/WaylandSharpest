using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// Server-side <c>wl_display</c>: the root object of a compositor, owning the
/// listening sockets, event loop, clients, and globals.
/// </summary>
public sealed unsafe class WlServerDisplay : IDisposable
{
    private nint _handle;
    private Exception? _dispatchException;

    private WlServerDisplay(nint handle)
    {
        _handle = handle;
        EventLoop = new WlEventLoop((nint)LibWaylandServer.wl_display_get_event_loop((wl_display*)handle), this);
    }

    public static WlServerDisplay Create()
    {
        var display = LibWaylandServer.wl_display_create();
        if (display == null)
        {
            throw new WaylandException("wl_display_create failed.");
        }

        return new WlServerDisplay((nint)display);
    }

    public nint RawHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    public WlEventLoop EventLoop { get; }

    /// <summary>Adds a socket with an automatically chosen name and returns it.</summary>
    public string AddSocketAuto()
    {
        var name = LibWaylandServer.wl_display_add_socket_auto((wl_display*)RawHandle);
        if (name == null)
        {
            throw new WaylandException("wl_display_add_socket_auto failed.");
        }

        return Marshal.PtrToStringUTF8((nint)name)!;
    }

    /// <summary>Adds a socket with an explicit name under <c>$XDG_RUNTIME_DIR</c>.</summary>
    public void AddSocket(string name)
    {
        var namePtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            if (LibWaylandServer.wl_display_add_socket((wl_display*)RawHandle, (sbyte*)namePtr) != 0)
            {
                throw new WaylandException($"wl_display_add_socket('{name}') failed.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    /// <summary>Creates a client for an already-connected socket fd.</summary>
    public WlClient CreateClient(int fd)
    {
        var client = LibWaylandServer.wl_client_create((wl_display*)RawHandle, fd);
        if (client == null)
        {
            throw new WaylandException($"wl_client_create failed for fd {fd}.");
        }

        return WlClient.Get((nint)client, this);
    }

    /// <summary>Publishes a global; <paramref name="onBind"/> is invoked when a client binds it.</summary>
    public WlGlobal CreateGlobal(WlInterfaceSpec iface, int version, WlGlobal.BindHandler onBind) =>
        new(this, iface, version, onBind);

    /// <summary>Runs the event loop until <see cref="Terminate"/> is called.</summary>
    public void Run() => LibWaylandServer.wl_display_run((wl_display*)RawHandle);

    public void Terminate() => LibWaylandServer.wl_display_terminate((wl_display*)RawHandle);

    public void FlushClients() => LibWaylandServer.wl_display_flush_clients((wl_display*)RawHandle);

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
        if (_handle == 0)
        {
            return;
        }

        LibWaylandServer.wl_display_destroy_clients((wl_display*)_handle);
        LibWaylandServer.wl_display_destroy((wl_display*)_handle);
        _handle = 0;
    }
}
