namespace Wayland.Server.Managed;

/// <summary>
/// The three interfaces a connection serves itself. They are written out here
/// rather than taken from the generated protocol so that the bootstrap has
/// nothing beneath it, and so a compositor's own handlers can never intercept
/// them.
/// </summary>
internal static class WlCoreInterfaces
{
    /// <summary>Object id 1, which exists before either end has said anything.</summary>
    internal static readonly WlInterfaceSpec Display = new(
        "wl_display",
        1,
        [
            new WlMessageSpec("sync", "n", [static () => Callback!]),
            new WlMessageSpec("get_registry", "n", [static () => Registry!]),
        ],
        [
            new WlMessageSpec("error", "ous", [null, null, null]),
            new WlMessageSpec("delete_id", "u", [null]),
        ]);

    /// <summary>
    /// The <c>bind</c> request's new object is untyped, which on the wire means
    /// the interface name and version precede the id.
    /// </summary>
    internal static readonly WlInterfaceSpec Registry = new(
        "wl_registry",
        1,
        [
            new WlMessageSpec("bind", "usun", [null, null, null, null]),
        ],
        [
            new WlMessageSpec("global", "usu", [null, null, null]),
            new WlMessageSpec("global_remove", "u", [null]),
        ]);

    internal static readonly WlInterfaceSpec Callback = new(
        "wl_callback",
        1,
        [],
        [
            new WlMessageSpec("done", "u", [null]),
        ]);

    internal const uint DisplaySyncOpcode = 0;
    internal const uint DisplayGetRegistryOpcode = 1;
    internal const uint DisplayErrorOpcode = 0;
    internal const uint DisplayDeleteIdOpcode = 1;

    internal const uint RegistryBindOpcode = 0;
    internal const uint RegistryGlobalOpcode = 0;
    internal const uint RegistryGlobalRemoveOpcode = 1;

    internal const uint CallbackDoneOpcode = 0;
}
