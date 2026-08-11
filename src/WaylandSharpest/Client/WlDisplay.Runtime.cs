using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland;

/// <summary>
/// Hand-written half of the client connection object; the protocol requests and
/// events are generated from <c>wayland.xml</c> into the other partial.
/// </summary>
public sealed unsafe partial class WlDisplay
{
    private Exception? _dispatchException;

    private WlDisplay(nint handle) : base(handle, null)
    {
    }

    /// <summary>
    /// Connects to a Wayland compositor. <paramref name="name"/> follows
    /// <c>wl_display_connect</c> semantics (null means <c>$WAYLAND_DISPLAY</c>).
    /// </summary>
    public static WlDisplay Connect(string? name = null)
    {
        var namePtr = AllocString(name);
        try
        {
            var display = LibWaylandClient.wl_display_connect((sbyte*)namePtr);
            if (display == null)
            {
                throw new WaylandException($"Failed to connect to Wayland display '{name ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(default)"}'.");
            }

            // Connect builds the root proxy directly rather than through
            // CreateWrapped, so it registers itself.
            var wrapper = new WlDisplay((nint)display);
            wrapper.Register();
            return wrapper;
        }
        finally
        {
            FreeString(namePtr);
        }
    }

    /// <summary>Wraps an already-connected socket, taking ownership of <paramref name="fd"/>.</summary>
    public static WlDisplay ConnectToFd(int fd)
    {
        var display = LibWaylandClient.wl_display_connect_to_fd(fd);
        if (display == null)
        {
            throw new WaylandException($"Failed to create Wayland display from fd {fd}.");
        }

        var wrapper = new WlDisplay((nint)display);
        wrapper.Register();
        return wrapper;
    }

    /// <summary>The connection's file descriptor, for external event loops.</summary>
    public int Fd => LibWaylandClient.wl_display_get_fd((wl_display*)RawHandle);

    /// <summary>
    /// Releases an fd-slot value an event on this connection delivered. For the
    /// libwayland transport this is <c>close(2)</c>; a channel transport
    /// releases the token. Never call <c>close(2)</c> directly on an
    /// event-delivered fd-slot value.
    /// </summary>
    public void CloseFd(int fd)
    {
        if (fd >= 0)
        {
            _ = close(fd);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", ExactSpelling = true)]
    private static extern int close(int fd);

    /// <summary>
    /// Creates an event queue. Proxies assigned to it deliver their events only
    /// when that queue is dispatched, which is what makes a second thread
    /// possible. The queue must outlive every proxy assigned to it.
    /// </summary>
    /// <param name="name">
    /// A debug label, used where the loaded libwayland supports named queues
    /// (1.23+) and ignored otherwise.
    /// </param>
    public WlEventQueue CreateQueue(string? name = null) => new(this, name);

    /// <summary>Blocks until all pending requests have been processed by the compositor.</summary>
    public void Roundtrip() => AfterDispatch(LibWaylandClient.wl_display_roundtrip((wl_display*)RawHandle));

    /// <summary>Dispatches events, blocking until at least one arrives.</summary>
    public void Dispatch() => AfterDispatch(LibWaylandClient.wl_display_dispatch((wl_display*)RawHandle));

    /// <summary>Dispatches already-queued events without blocking or reading the socket.</summary>
    public void DispatchPending() => AfterDispatch(LibWaylandClient.wl_display_dispatch_pending((wl_display*)RawHandle));

    /// <summary>Flushes buffered requests to the compositor.</summary>
    public void Flush()
    {
        if (LibWaylandClient.wl_display_flush((wl_display*)RawHandle) < 0)
        {
            ThrowConnectionError();
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for events and reads them into
    /// their queues without dispatching; returns false on timeout. Runs the full
    /// prepare/poll/read protocol correctly, so this is the safe default for
    /// hosting the connection inside another loop. Call
    /// <see cref="DispatchPending"/> (or the queue's) afterwards.
    /// </summary>
    /// <param name="timeoutMs">Milliseconds to wait; negative blocks indefinitely.</param>
    /// <param name="queue">The queue to read for, or null for the default queue.</param>
    public bool TryReadEvents(int timeoutMs, WlEventQueue? queue = null)
    {
        // Pending events mean there is nothing to wait for: report them as
        // readable so the caller dispatches instead of blocking.
        if (!TryPrepareRead(queue))
        {
            return true;
        }

        try
        {
            Flush();
        }
        catch
        {
            CancelRead();
            throw;
        }

        var poll = new pollfd { fd = Fd, events = POLLIN };
        int ready;
        do
        {
            ready = LibC.poll(&poll, 1, timeoutMs);
        }
        while (ready < 0 && Marshal.GetLastPInvokeError() == EINTR);

        if (ready <= 0)
        {
            CancelRead();
            if (ready < 0)
            {
                throw new WaylandException($"poll on the Wayland connection failed (errno {Marshal.GetLastPInvokeError()}).");
            }

            return false;
        }

        ReadEvents();
        return true;
    }

    /// <summary>
    /// Announces intent to read. Returns false when the queue already has
    /// undispatched events — dispatch them and retry.
    /// </summary>
    /// <remarks>
    /// Advanced. On true you <strong>must</strong> follow with
    /// <see cref="ReadEvents"/> or <see cref="CancelRead"/>: a prepared read that
    /// is never resolved blocks every other thread reading this connection, and
    /// that hang has no local symptom. Prefer <see cref="TryReadEvents"/> unless
    /// you are integrating with a host loop whose prepare and check phases live
    /// in different callbacks.
    /// </remarks>
    public bool TryPrepareRead(WlEventQueue? queue = null)
    {
        // Nonzero means one thing only: the queue still holds undispatched
        // events. libwayland's own documented idiom loops on it rather than
        // treating it as an error.
        var result = queue is null
            ? LibWaylandClient.wl_display_prepare_read((wl_display*)RawHandle)
            : LibWaylandClient.wl_display_prepare_read_queue(
                (wl_display*)RawHandle, (wl_event_queue*)queue.RawHandle);
        return result == 0;
    }

    /// <summary>
    /// Reads from the socket into the queues. Only valid after
    /// <see cref="TryPrepareRead"/> returned true.
    /// </summary>
    public void ReadEvents()
    {
        if (LibWaylandClient.wl_display_read_events((wl_display*)RawHandle) < 0)
        {
            ThrowConnectionError();
        }
    }

    /// <summary>Abandons a prepared read without reading.</summary>
    public void CancelRead() => LibWaylandClient.wl_display_cancel_read((wl_display*)RawHandle);

    /// <summary>
    /// Dispatches the default queue, blocking at most
    /// <paramref name="timeoutMs"/> milliseconds.
    /// </summary>
    public void Dispatch(int timeoutMs)
    {
        if (HasDispatchTimeout)
        {
            var timeout = new Timespec
            {
                Seconds = timeoutMs / 1000,
                Nanoseconds = (timeoutMs % 1000) * 1_000_000,
            };
            AfterDispatch(LibWaylandClient.wl_display_dispatch_timeout((wl_display*)RawHandle, (timespec*)&timeout));
            return;
        }

        if (TryReadEvents(timeoutMs))
        {
            DispatchPending();
        }
    }

    /// <summary>wl_display_dispatch_timeout is libwayland 1.23.91.</summary>
    private static readonly bool HasDispatchTimeout = NativeFeatures.ClientHas("wl_display_dispatch_timeout");

    private const int EINTR = 4;
    private const short POLLIN = 1;

    internal void CaptureDispatchException(Exception exception) => _dispatchException ??= exception;

    private void AfterDispatch(int result)
    {
        if (_dispatchException is { } pending)
        {
            _dispatchException = null;
            throw new WaylandException("An event handler threw during dispatch.", pending);
        }

        if (result < 0)
        {
            ThrowConnectionError();
        }
    }

    internal void ThrowConnectionError()
    {
        var error = LibWaylandClient.wl_display_get_error((wl_display*)RawHandle);
        wl_interface* iface = null;
        uint objectId = 0;
        var protocolError = LibWaylandClient.wl_display_get_protocol_error((wl_display*)RawHandle, &iface, &objectId);
        if (protocolError != 0 || iface != null)
        {
            var interfaceName = iface == null ? null : Marshal.PtrToStringAnsi((nint)iface->name);
            throw new WaylandProtocolException(
                $"Wayland protocol error {protocolError} on {interfaceName ?? "?"}@{objectId}.",
                (int)protocolError,
                interfaceName,
                objectId);
        }

        throw new WaylandException($"Wayland connection error (errno {error}).");
    }

    /// <summary>Disconnects from the compositor and invalidates all objects on this connection.</summary>
    protected override void DisposeCore()
    {
        LibWaylandClient.wl_display_disconnect((wl_display*)RawHandle);
        MarkDestroyed();
    }
}
