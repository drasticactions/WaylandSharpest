using System.Runtime.InteropServices;

namespace Wayland.Native;

/// <summary>
/// Runtime probes for libwayland entry points that post-date the oldest
/// supported release. Each probe costs one <c>dlsym</c> per process; callers are
/// expected to cache the result in a <c>static readonly bool</c>.
/// </summary>
internal static class NativeFeatures
{
    private static readonly Lazy<nint> ServerLibrary = new(() => TryLoad("libwayland-server.so.0", "libwayland-server.so"));
    private static readonly Lazy<nint> ClientLibrary = new(() => TryLoad("libwayland-client.so.0", "libwayland-client.so"));

    /// <summary>Whether <c>libwayland-server</c> exports <paramref name="symbol"/>.</summary>
    internal static bool ServerHas(string symbol) => Has(ServerLibrary.Value, symbol);

    /// <summary>Whether <c>libwayland-client</c> exports <paramref name="symbol"/>.</summary>
    internal static bool ClientHas(string symbol) => Has(ClientLibrary.Value, symbol);

    private static bool Has(nint library, string symbol) =>
        library != 0 && NativeLibrary.TryGetExport(library, symbol, out _);

    private static nint TryLoad(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return 0;
    }
}
