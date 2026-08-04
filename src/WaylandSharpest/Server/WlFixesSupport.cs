using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// libwayland-server's handling of the <c>wl_fixes</c> interface. Like
/// <see cref="LibWaylandShm"/> this bypasses the transport seam: the bookkeeping
/// it performs lives inside libwayland's own registry state, which a managed
/// transport would keep itself.
///
/// libwayland does <em>not</em> create the <c>wl_fixes</c> global for you — a
/// compositor that wants it publishes <c>WlFixes.Interface</c> and constructs a
/// <see cref="WlFixesResource"/> on bind, then routes the requests here.
/// </summary>
public static unsafe class WlFixesSupport
{
    /// <summary>
    /// True when the loaded libwayland-server implements the <c>wl_fixes</c>
    /// acknowledgement handling (1.26+).
    /// </summary>
    public static bool IsSupported { get; } = NativeFeatures.ServerHas("wl_fixes_handle_ack_global_remove");

    /// <summary>
    /// Services a client's <c>wl_fixes.ack_global_remove</c> request: the
    /// registry stops treating <paramref name="globalName"/> as bindable for
    /// that client, which is what lets a removed global report itself withdrawn.
    /// </summary>
    /// <param name="fixes">The client's <c>wl_fixes</c> object.</param>
    /// <param name="registryResourceHandle">
    /// Raw <c>wl_resource*</c> of the client's <c>wl_registry</c>, as carried by
    /// the request's object argument. libwayland owns the registry object, so
    /// this is a handle rather than a wrapper.
    /// </param>
    /// <param name="globalName">The registry name the client is acknowledging.</param>
    /// <exception cref="WaylandException">The loaded libwayland is older than 1.26.</exception>
    public static void HandleAckGlobalRemove(WlFixesResource fixes, nint registryResourceHandle, uint globalName)
    {
        ArgumentNullException.ThrowIfNull(fixes);
        if (!IsSupported)
        {
            throw new WaylandException(
                "wl_fixes_handle_ack_global_remove requires libwayland 1.26 or newer; the loaded libwayland-server.so.0 does not export it.");
        }

        LibWaylandServer.wl_fixes_handle_ack_global_remove(
            (wl_resource*)fixes.RawHandle, (wl_resource*)registryResourceHandle, globalName);
    }
}
