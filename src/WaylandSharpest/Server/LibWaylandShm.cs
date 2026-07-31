using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// Implementation of <c>wl_shm</c>.
/// </summary>
public static unsafe class LibWaylandShm
{
    /// <summary>
    /// Creates the <c>wl_shm</c> global, advertising <c>argb8888</c> and
    /// <c>xrgb8888</c>. The display must be on the libwayland transport.
    /// </summary>
    public static void Init(WlServerDisplay display)
    {
        if (LibWaylandServer.wl_display_init_shm((wl_display*)display.RawHandle) != 0)
        {
            throw new WaylandException("wl_display_init_shm failed.");
        }
    }

    /// <summary>Advertises an additional shm format.</summary>
    public static void AddFormat(WlServerDisplay display, uint format)
    {
        if (LibWaylandServer.wl_display_add_shm_format((wl_display*)display.RawHandle, format) == null)
        {
            throw new WaylandException("wl_display_add_shm_format failed.");
        }
    }

    /// <summary>
    /// Accessor for the <c>wl_shm_buffer</c> behind a client's <c>wl_buffer</c>
    /// resource, or <c>null</c> if the resource is not an shm buffer.
    /// </summary>
    public static WlShmBufferRef? FromResource(nint bufferResourceHandle)
    {
        var buffer = LibWaylandServer.wl_shm_buffer_get((wl_resource*)bufferResourceHandle);
        return buffer == null ? null : new WlShmBufferRef((nint)buffer);
    }
}

/// <summary>
/// A non-owning view of a client's shm buffer. Data access must be bracketed by
/// <see cref="BeginAccess"/>/<see cref="EndAccess"/>, which is what protects the
/// compositor from a client shrinking the pool mid-read.
/// </summary>
public readonly unsafe struct WlShmBufferRef
{
    private readonly wl_shm_buffer* _buffer;

    internal WlShmBufferRef(nint buffer) => _buffer = (wl_shm_buffer*)buffer;

    public int Width => LibWaylandServer.wl_shm_buffer_get_width(_buffer);

    public int Height => LibWaylandServer.wl_shm_buffer_get_height(_buffer);

    public int Stride => LibWaylandServer.wl_shm_buffer_get_stride(_buffer);

    public uint Format => LibWaylandServer.wl_shm_buffer_get_format(_buffer);

    public void BeginAccess() => LibWaylandServer.wl_shm_buffer_begin_access(_buffer);

    public void EndAccess() => LibWaylandServer.wl_shm_buffer_end_access(_buffer);

    /// <summary>Pool data pointer; only valid between Begin/EndAccess.</summary>
    public nint Data => (nint)LibWaylandServer.wl_shm_buffer_get_data(_buffer);

    public WlShmPoolRef RefPool() => new((nint)LibWaylandServer.wl_shm_buffer_ref_pool(_buffer));
}

/// <summary>A reference on an shm pool's mapping; release exactly once.</summary>
public readonly unsafe struct WlShmPoolRef
{
    private readonly nint _pool;

    internal WlShmPoolRef(nint pool) => _pool = pool;

    public bool IsValid => _pool != 0;

    public void Unref()
    {
        if (_pool != 0)
        {
            LibWaylandServer.wl_shm_pool_unref((wl_shm_pool*)_pool);
        }
    }
}

/// <summary>
/// Operations on <c>wl_resource</c>s owned by another implementation sharing the
/// display. Bypasses the
/// transport seam by definition.
/// </summary>
public static unsafe class WlForeignResource
{
    public static uint GetId(nint resourceHandle) =>
        LibWaylandServer.wl_resource_get_id((wl_resource*)resourceHandle);

    public static uint GetVersion(nint resourceHandle) =>
        (uint)LibWaylandServer.wl_resource_get_version((wl_resource*)resourceHandle);

    /// <summary>Posts an event on a resource this library does not own.</summary>
    public static void PostEvent(nint resourceHandle, uint opcode, scoped ReadOnlySpan<WlArg> args)
    {
        fixed (WlArg* argsPtr = args)
        {
            LibWaylandServer.wl_resource_post_event_array(
                (wl_resource*)resourceHandle, opcode, (wl_argument*)argsPtr);
        }
    }

    /// <summary>
    /// Invokes <paramref name="callback"/> when the resource is destroyed.
    /// Dispose the registration to cancel; disposing after the resource died is
    /// a no-op. The callback runs inside native signal emission and must not
    /// throw.
    /// </summary>
    public static WlForeignDestroyListener AddDestroyListener(nint resourceHandle, Action callback) =>
        new(resourceHandle, callback);
}

/// <summary>A destroy-listener registration on a foreign resource.</summary>
public sealed unsafe class WlForeignDestroyListener : IDisposable
{
    private readonly Action _callback;
    private ListenerBlock* _block;
    private GCHandle _selfHandle;

    internal WlForeignDestroyListener(nint resourceHandle, Action callback)
    {
        _callback = callback;
        _selfHandle = GCHandle.Alloc(this);
        _block = (ListenerBlock*)Marshal.AllocHGlobal(sizeof(ListenerBlock));
        _block->Listener = default;
        _block->Listener.notify = &OnDestroyed;
        _block->Self = GCHandle.ToIntPtr(_selfHandle);
        LibWaylandServer.wl_resource_add_destroy_listener((wl_resource*)resourceHandle, &_block->Listener);
    }

    public void Dispose()
    {
        if (_block == null)
        {
            return;
        }

        // Unlink from the resource's destroy signal by hand; wl_list_remove is
        // inline in the C headers on some platforms, and the surgery is trivial.
        var link = &_block->Listener.link;
        link->prev->next = link->next;
        link->next->prev = link->prev;
        Release();
    }

    private void Release()
    {
        Marshal.FreeHGlobal((nint)_block);
        _block = null;
        _selfHandle.Free();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ListenerBlock
    {
        public wl_listener Listener;
        public nint Self;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDestroyed(wl_listener* listener, void* data)
    {
        var block = (ListenerBlock*)listener;
        if (GCHandle.FromIntPtr(block->Self).Target is WlForeignDestroyListener registration)
        {
            // Release first: the callback may throw, and the signal emit frees
            // nothing on our behalf.
            var callback = registration._callback;
            registration.Release();
            callback();
        }
    }
}
