using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Wayland.Native;

/// <summary>
/// Maps the logical library names used by the generated bindings to the
/// versioned sonames shipped by distributions.
/// </summary>
internal static class NativeLibraryResolver
{
    [ModuleInitializer]
    internal static void Initialize() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);

    private static nint Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        var candidates = libraryName switch
        {
            "wayland-client" => new[] { "libwayland-client.so.0", "libwayland-client.so" },
            "wayland-server" => new[] { "libwayland-server.so.0", "libwayland-server.so" },
            "wayland-egl" => new[] { "libwayland-egl.so.1", "libwayland-egl.so" },
            "wayland-cursor" => new[] { "libwayland-cursor.so.0", "libwayland-cursor.so" },
            _ => null,
        };

        if (candidates is not null)
        {
            foreach (var candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                {
                    return handle;
                }
            }
        }

        return nint.Zero;
    }
}
