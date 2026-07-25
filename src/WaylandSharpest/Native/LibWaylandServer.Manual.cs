using System.Runtime.InteropServices;

namespace Wayland.Native;

public static unsafe partial class LibWaylandServer
{
    /// <summary>
    /// <c>wl_resource_post_error</c> is variadic and has no array-based
    /// counterpart. Declaring it with a fixed <c>(format, message)</c> tail and
    /// always calling it as <c>("%s", message)</c> is safe on the System V x86-64
    /// and AArch64 calling conventions, where the first integer/pointer varargs
    /// travel in the same registers as named parameters.
    /// </summary>
    [DllImport("wayland-server", EntryPoint = "wl_resource_post_error", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void wl_resource_post_error_fixed(
        wl_resource* resource,
        uint code,
        sbyte* format,
        sbyte* message);
}
