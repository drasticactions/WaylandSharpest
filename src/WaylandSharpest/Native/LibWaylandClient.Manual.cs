using System.Runtime.InteropServices;

namespace Wayland.Native;

public static unsafe partial class LibWaylandClient
{
    /// <summary>
    /// The client half of libwayland's log hook. See
    /// <see cref="LibWaylandServer.wl_log_set_handler_server"/> for why the
    /// <c>va_list</c> parameter is declared as a plain pointer.
    /// </summary>
    [DllImport("wayland-client", EntryPoint = "wl_log_set_handler_client", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void wl_log_set_handler_client(
        delegate* unmanaged[Cdecl]<sbyte*, nint, void> handler);
}

/// <summary>
/// The C library entry points the runtime needs directly. The binding is
/// Linux-only by soname resolution, so libc is always present.
/// </summary>
internal static unsafe partial class LibC
{
    [DllImport("libc", EntryPoint = "vsnprintf", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int vsnprintf(byte* buffer, nuint size, sbyte* format, nint args);
}
