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

    /// <summary>Waits for readiness on the connection fd during the prepare/read protocol.</summary>
    [DllImport("libc", EntryPoint = "poll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, SetLastError = true)]
    internal static extern int poll(pollfd* fds, nuint count, int timeoutMs);
}

/// <summary>Managed mirror of <c>struct pollfd</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct pollfd
{
    public int fd;
    public short events;
    public short revents;
}

/// <summary>
/// Managed mirror of <c>struct timespec</c>. ClangSharp emits the type as an
/// empty stub because the header only forward-declares it, so the layout is
/// spelled out here; <c>time_t</c> and <c>long</c> are both pointer-sized on the
/// architectures this library supports.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Timespec
{
    public nint Seconds;
    public nint Nanoseconds;
}
