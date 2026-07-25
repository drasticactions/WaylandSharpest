using System.Collections.Concurrent;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// A connected client on the server side (<c>wl_client</c>). Instances are
/// interned per native pointer so resource callbacks observe stable identity.
/// </summary>
public sealed unsafe class WlClient
{
    private static readonly ConcurrentDictionary<nint, WlClient> Instances = new();

    private WlClient(nint handle, WlServerDisplay? display)
    {
        RawHandle = handle;
        Display = display;
    }

    public nint RawHandle { get; }

    /// <summary>The owning display, when known (clients created through WaylandSharpest APIs).</summary>
    public WlServerDisplay? Display { get; }

    internal static WlClient Get(nint handle, WlServerDisplay? display) =>
        Instances.GetOrAdd(handle, static (h, d) => new WlClient(h, d), display);

    public void Flush() => LibWaylandServer.wl_client_flush((wl_client*)RawHandle);

    /// <summary>Forcibly disconnects the client.</summary>
    public void Destroy()
    {
        Instances.TryRemove(RawHandle, out _);
        LibWaylandServer.wl_client_destroy((wl_client*)RawHandle);
    }
}
