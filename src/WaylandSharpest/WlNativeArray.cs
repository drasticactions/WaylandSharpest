using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland;

/// <summary>
/// Scoped native <c>wl_array</c> holding a copy of managed bytes for the duration
/// of a request or event marshal. Used by generated code for array arguments.
/// </summary>
public unsafe ref struct WlNativeArray
{
    private nint _memory;

    /// <summary>Pointer to the native <c>wl_array</c>, for <see cref="WlArg.Ptr"/>.</summary>
    public readonly nint Pointer => _memory;

    public static WlNativeArray Create(ReadOnlySpan<byte> data)
    {
        var memory = Marshal.AllocHGlobal(sizeof(wl_array) + data.Length);
        var array = (wl_array*)memory;
        array->size = (nuint)data.Length;
        array->alloc = (nuint)data.Length;
        array->data = (byte*)memory + sizeof(wl_array);
        data.CopyTo(new Span<byte>(array->data, data.Length));
        return new WlNativeArray { _memory = memory };
    }

    public void Dispose()
    {
        if (_memory != 0)
        {
            Marshal.FreeHGlobal(_memory);
            _memory = 0;
        }
    }
}
