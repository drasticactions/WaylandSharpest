using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// The server display's event loop. Owned by the display; not disposable here.
/// </summary>
public sealed unsafe class WlEventLoop
{
    private readonly WlServerDisplay _display;

    internal WlEventLoop(nint handle, WlServerDisplay display)
    {
        RawHandle = handle;
        _display = display;
    }

    public nint RawHandle { get; }

    /// <summary>
    /// Dispatches pending server work. <paramref name="timeoutMs"/>: 0 = poll,
    /// -1 = block until activity. Rethrows exceptions thrown by request handlers.
    /// </summary>
    public void Dispatch(int timeoutMs)
    {
        var result = LibWaylandServer.wl_event_loop_dispatch((wl_event_loop*)RawHandle, timeoutMs);
        _display.RethrowPendingDispatchException();
        if (result < 0)
        {
            throw new WaylandException("wl_event_loop_dispatch failed.");
        }
    }
}
