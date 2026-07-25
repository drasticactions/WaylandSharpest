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

            return new WlDisplay((nint)display);
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

        return new WlDisplay((nint)display);
    }

    /// <summary>The connection's file descriptor, for external event loops.</summary>
    public int Fd => LibWaylandClient.wl_display_get_fd((wl_display*)RawHandle);

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

    private void ThrowConnectionError()
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
