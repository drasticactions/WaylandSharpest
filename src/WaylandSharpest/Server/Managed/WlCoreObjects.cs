using System.Runtime.InteropServices;

namespace Wayland.Server.Managed;

/// <summary>
/// Object id 1. Its two requests are answered here rather than raised to the
/// compositor, so the bootstrap works before any global exists.
/// </summary>
internal sealed class WlDisplayObject : WlObject
{
    private readonly ManagedClient _client;

    internal WlDisplayObject(ManagedClient client)
        : base(WlObjectIds.Display, 1, WlCoreInterfaces.Display)
    {
        _client = client;
    }

    internal override void DispatchRequest(uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args)
    {
        switch (opcode)
        {
            case WlCoreInterfaces.DisplaySyncOpcode:
                _client.HandleSync(args[0].U);
                break;

            case WlCoreInterfaces.DisplayGetRegistryOpcode:
                _client.HandleGetRegistry(args[0].U);
                break;
        }
    }
}

/// <summary>
/// A client's view of the globals. One client may hold several, and each is
/// told about globals separately, because a filter can hide a global from a
/// client without hiding it from the next.
/// </summary>
internal sealed class WlRegistryObject : WlObject
{
    private readonly ManagedClient _client;

    internal WlRegistryObject(ManagedClient client, uint id)
        : base(id, 1, WlCoreInterfaces.Registry)
    {
        _client = client;
    }

    internal ManagedClient Client => _client;

    internal override void DispatchRequest(uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args)
    {
        if (opcode == WlCoreInterfaces.RegistryBindOpcode)
        {
            _client.HandleBind(this, args[0].U, Marshal.PtrToStringUTF8(args[1].Ptr), args[2].U, args[3].U);
        }
    }

    /// <summary>Tells this registry about a global.</summary>
    internal void SendGlobal(ManagedGlobal global)
    {
        var name = Marshal.StringToCoTaskMemUTF8(global.Spec.Name);
        try
        {
            Span<WlArg> args = stackalloc WlArg[3];
            args[0].U = global.Name;
            args[1].Ptr = name;
            args[2].U = global.AdvertisedVersion;
            _client.WriteEvent(this, WlCoreInterfaces.RegistryGlobalOpcode, WlCoreInterfaces.Registry.Events[0].Wire, args);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }

        global.Announced(this);
    }

    internal void SendGlobalRemove(ManagedGlobal global)
    {
        Span<WlArg> args = stackalloc WlArg[1];
        args[0].U = global.Name;
        _client.WriteEvent(this, WlCoreInterfaces.RegistryGlobalRemoveOpcode, WlCoreInterfaces.Registry.Events[1].Wire, args);
    }
}
