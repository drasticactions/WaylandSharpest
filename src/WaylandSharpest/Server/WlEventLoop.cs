namespace Wayland.Server;

/// <summary>
/// The server display's event loop. Owned by the display; not disposable here.
/// </summary>
public sealed class WlEventLoop
{
    private readonly IWlEventLoop _impl;
    private readonly WlServerDisplay _display;

    internal WlEventLoop(IWlEventLoop impl, WlServerDisplay display)
    {
        _impl = impl;
        _display = display;
    }

    public nint RawHandle => _impl.RawHandle;

    /// <summary>
    /// Dispatches pending server work. <paramref name="timeoutMs"/>: 0 = poll,
    /// -1 = block until activity. Rethrows exceptions thrown by request handlers.
    /// </summary>
    public void Dispatch(int timeoutMs)
    {
        var result = _impl.Dispatch(timeoutMs);
        _display.RethrowPendingDispatchException();
        if (result < 0)
        {
            throw new WaylandException("Event loop dispatch failed.");
        }
    }
}
