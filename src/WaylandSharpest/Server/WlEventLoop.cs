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
    /// The loop's pollable file descriptor. Readable means work is pending: poll
    /// it from a host event loop (GLib, libuv, a .NET host) and call
    /// <see cref="Dispatch"/> with a zero timeout when it fires. This is what
    /// lets the Wayland loop run inside another loop instead of owning the
    /// main thread.
    /// </summary>
    public int Fd => _impl.Fd;

    /// <summary>
    /// Dispatches pending server work. <paramref name="timeoutMs"/>: 0 = poll,
    /// -1 = block until activity. Rethrows exceptions thrown by request handlers
    /// and event-source callbacks.
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

    /// <summary>
    /// Watches a file descriptor; the handler runs during <see cref="Dispatch"/>
    /// whenever <paramref name="events"/> are ready. The caller keeps ownership
    /// of the fd and must remove the source before closing it.
    /// </summary>
    public WlEventSource AddFd(int fd, WlFdEvents events, Action<int, WlFdEvents> handler) =>
        new(_impl.AddFd(fd, events, (f, e) =>
        {
            try
            {
                handler(f, e);
            }
            catch (Exception ex)
            {
                _display.CaptureDispatchException(ex);
            }
        }));

    /// <summary>Adds a disarmed timer; arm it with <see cref="WlEventSource.UpdateTimer"/>.</summary>
    public WlEventSource AddTimer(Action handler) =>
        new(_impl.AddTimer(() =>
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _display.CaptureDispatchException(ex);
            }
        }));

    /// <summary>
    /// Queues a one-shot callback that runs before the loop next goes to sleep.
    /// </summary>
    public WlEventSource AddIdle(Action handler) =>
        new(_impl.AddIdle(() =>
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _display.CaptureDispatchException(ex);
            }
        }));

    /// <summary>
    /// Handles <paramref name="signalNumber"/> (<c>SIGTERM</c> is 15) on the
    /// loop thread via signalfd. libwayland blocks the signal in the calling
    /// thread's mask, so register before spawning threads that must not receive
    /// it. Unlike <c>PosixSignalRegistration</c>, which delivers on a threadpool
    /// thread, the handler runs where Wayland calls are legal.
    /// </summary>
    public WlEventSource AddSignal(int signalNumber, Action<int> handler) =>
        new(_impl.AddSignal(signalNumber, signal =>
        {
            try
            {
                handler(signal);
            }
            catch (Exception ex)
            {
                _display.CaptureDispatchException(ex);
            }
        }));

    /// <summary>Runs pending idle callbacks without waiting for events.</summary>
    public void DispatchIdle()
    {
        _impl.DispatchIdle();
        _display.RethrowPendingDispatchException();
    }
}

/// <summary>A registered event-loop source; remove it to stop callbacks.</summary>
public sealed class WlEventSource
{
    private readonly IWlEventSource _impl;

    internal WlEventSource(IWlEventSource impl)
    {
        _impl = impl;
    }

    /// <summary>True once removed, or after a one-shot source has fired.</summary>
    public bool IsRemoved => _impl.IsRemoved;

    public void Remove() => _impl.Remove();

    /// <summary>Arms (delay in ms) or disarms (0) a timer source.</summary>
    public void UpdateTimer(int delayMs) => _impl.UpdateTimer(delayMs);

    /// <summary>Changes the watched events of an fd source.</summary>
    public void UpdateFd(WlFdEvents events) => _impl.UpdateFd(events);
}
