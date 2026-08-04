using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland;

/// <summary>
/// An event queue. Events for a proxy assigned to this queue are delivered only
/// when this queue is dispatched, which is what lets a second thread — a render
/// thread, a media pipeline, a WSI implementation — service its own objects
/// without stealing the main thread's events.
/// </summary>
/// <remarks>
/// The queue must outlive every proxy assigned to it: dispose it last. Disposing
/// one that still has live proxies throws rather than handing libwayland a queue
/// its objects still point at.
/// </remarks>
public sealed unsafe class WlEventQueue : IDisposable
{
    /// <summary>
    /// Queues keyed by native pointer, so a proxy that inherited its queue
    /// inside libwayland can be mapped back to the managed wrapper. Mirrors the
    /// pointer-keyed registry <see cref="WlProxy"/> uses for the same reason.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, WlEventQueue> Owned = new();

    /// <summary>
    /// Naming queues arrived in libwayland 1.22.91; without it the name is a
    /// managed-side label only.
    /// </summary>
    private static readonly bool HasNamedQueues =
        NativeFeatures.ClientHas("wl_display_create_queue_with_name");

    private readonly WlDisplay _display;
    private nint _handle;
    private int _assignedProxyCount;
    private Exception? _dispatchException;

    internal WlEventQueue(WlDisplay display, string? name)
    {
        _display = display;
        wl_event_queue* queue;
        if (name is not null && HasNamedQueues)
        {
            var namePtr = Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                queue = LibWaylandClient.wl_display_create_queue_with_name(
                    (wl_display*)display.RawHandle, (sbyte*)namePtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
            }
        }
        else
        {
            queue = LibWaylandClient.wl_display_create_queue((wl_display*)display.RawHandle);
        }

        if (queue == null)
        {
            throw new WaylandException("wl_display_create_queue failed.");
        }

        _handle = (nint)queue;

        // wl_event_queue_get_name arrived with named queues, so it cannot be
        // called to discover that naming is unsupported.
        Name = HasNamedQueues
            ? Marshal.PtrToStringUTF8((nint)LibWaylandClient.wl_event_queue_get_name(queue))
            : null;
        Owned[_handle] = this;
    }

    /// <summary>The queue's debug name, when the loaded libwayland supports naming (1.23+).</summary>
    public string? Name { get; }

    /// <summary>The native <c>wl_event_queue*</c> handle.</summary>
    public nint RawHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    /// <summary>The connection this queue belongs to.</summary>
    public WlDisplay Display => _display;

    /// <summary>
    /// Proxies currently assigned to this queue. Disposing with any still live
    /// is an error.
    /// </summary>
    public int AssignedProxyCount => _assignedProxyCount;

    /// <summary>True once the queue has been disposed.</summary>
    public bool IsDisposed => _handle == 0;

    /// <summary>Dispatches this queue, blocking until at least one of its events arrives.</summary>
    public void Dispatch() =>
        AfterDispatch(LibWaylandClient.wl_display_dispatch_queue(
            (wl_display*)_display.RawHandle, (wl_event_queue*)RawHandle));

    /// <summary>Dispatches this queue's already-queued events without blocking or reading the socket.</summary>
    public void DispatchPending() =>
        AfterDispatch(LibWaylandClient.wl_display_dispatch_queue_pending(
            (wl_display*)_display.RawHandle, (wl_event_queue*)RawHandle));

    /// <summary>Blocks until the compositor has processed all requests, dispatching this queue.</summary>
    public void Roundtrip() =>
        AfterDispatch(LibWaylandClient.wl_display_roundtrip_queue(
            (wl_display*)_display.RawHandle, (wl_event_queue*)RawHandle));

    /// <summary>
    /// Destroys the queue.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Proxies are still assigned to the queue; they must be destroyed or moved
    /// to another queue first.
    /// </exception>
    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        if (_assignedProxyCount > 0)
        {
            throw new InvalidOperationException(
                $"Cannot dispose event queue '{Name ?? "(unnamed)"}': {_assignedProxyCount} proxies are still assigned to it. Destroy them or move them to another queue first.");
        }

        Owned.TryRemove(_handle, out _);
        LibWaylandClient.wl_event_queue_destroy((wl_event_queue*)_handle);
        _handle = 0;
    }

    /// <summary>The managed wrapper for a native queue pointer, or null for the default queue.</summary>
    internal static WlEventQueue? FromHandle(nint handle) =>
        handle == 0 ? null : Owned.TryGetValue(handle, out var queue) ? queue : null;

    internal void Attach() => Interlocked.Increment(ref _assignedProxyCount);

    internal void Detach() => Interlocked.Decrement(ref _assignedProxyCount);

    /// <summary>
    /// Holds an exception thrown by a handler for a proxy on this queue, so it
    /// surfaces on the thread that dispatched it rather than on whichever
    /// thread dispatches next.
    /// </summary>
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
            _display.ThrowConnectionError();
        }
    }

    public override string ToString() =>
        _handle == 0 ? "WlEventQueue(disposed)" : $"WlEventQueue({Name ?? "unnamed"})";
}
