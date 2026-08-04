using System.Runtime.InteropServices;

namespace Wayland.Native;

/// <summary>
/// An unmanaged <c>wl_listener</c> block paired with a <see cref="GCHandle"/> to
/// the managed object the notify callback needs. libwayland links the listener
/// into a signal's list by pointer, so the block must live in unmanaged memory
/// and be unlinked before it is freed.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeListener
{
    public wl_listener Listener;

    /// <summary><see cref="GCHandle.ToIntPtr"/> of the managed target.</summary>
    public nint Payload;

    /// <summary>
    /// Allocates a block wired to <paramref name="notify"/> and pinning
    /// <paramref name="target"/>. The caller links it into a signal and is
    /// responsible for exactly one <see cref="Free"/> or <see cref="Unlink"/>.
    /// </summary>
    internal static NativeListener* Allocate(
        delegate* unmanaged[Cdecl]<wl_listener*, void*, void> notify,
        object target)
    {
        var block = (NativeListener*)Marshal.AllocHGlobal(sizeof(NativeListener));
        block->Listener = default;
        block->Listener.notify = notify;
        block->Payload = GCHandle.ToIntPtr(GCHandle.Alloc(target));
        return block;
    }

    /// <summary>
    /// Removes the block from the signal it is linked into, then frees it. Doing
    /// the list surgery by hand rather than calling <c>wl_list_remove</c>, which
    /// is inline in the C headers on some platforms.
    /// </summary>
    internal static void Unlink(NativeListener* block)
    {
        var link = &block->Listener.link;
        link->prev->next = link->next;
        link->next->prev = link->prev;
        Free(block);
    }

    /// <summary>
    /// Frees a block that is already unlinked — which is the case inside a
    /// notify callback for a one-shot listener, since the signal drops it.
    /// </summary>
    internal static void Free(NativeListener* block)
    {
        GCHandle.FromIntPtr(block->Payload).Free();
        Marshal.FreeHGlobal((nint)block);
    }

    /// <summary>The managed target of the block containing <paramref name="listener"/>.</summary>
    internal static T? Target<T>(wl_listener* listener) where T : class =>
        GCHandle.FromIntPtr(((NativeListener*)listener)->Payload).Target as T;
}
