using System.Runtime.InteropServices;

namespace Wayland;

/// <summary>
/// Managed mirror of <c>union wl_argument</c>. Generated protocol code builds
/// spans of these without requiring unsafe code in consumer projects; the layout
/// is identical to the native union so the runtime can pass them straight to
/// libwayland.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct WlArg
{
    /// <summary>Signed integer argument (<c>i</c>).</summary>
    [FieldOffset(0)] public int I;

    /// <summary>Unsigned integer argument (<c>u</c>), also new-object ids (<c>n</c>) on the server side.</summary>
    [FieldOffset(0)] public uint U;

    /// <summary>Fixed-point argument (<c>f</c>).</summary>
    [FieldOffset(0)] public WlFixed F;

    /// <summary>Pointer-sized argument: strings (<c>s</c>), objects (<c>o</c>), arrays (<c>a</c>).</summary>
    [FieldOffset(0)] public nint Ptr;

    /// <summary>File-descriptor argument (<c>h</c>).</summary>
    [FieldOffset(0)] public int Fd;
}
