namespace Wayland.Server.Managed;

/// <summary>
/// A protocol object a compositor owns, standing between the wire and the
/// <see cref="WlResource"/> that generated code binds to.
/// </summary>
internal sealed class ManagedResource : WlObject, IWlResource
{
    private readonly ManagedClient _client;
    private readonly WlResource _owner;
    private bool _destroyed;

    internal ManagedResource(ManagedClient client, WlResource owner, WlInterfaceSpec spec, uint version, uint id)
        : base(id, version, spec)
    {
        _client = client;
        _owner = owner;
    }

    public nint RawHandle => Handle;

    uint IWlResource.Id => Id;

    uint IWlResource.Version => Version;

    internal bool IsDestroyed => _destroyed;

    public void PostEvent(uint opcode, ReadOnlySpan<WlArg> args)
    {
        if (_destroyed)
        {
            return;
        }

        if (opcode >= (uint)Spec.Events.Count)
        {
            throw new WaylandException($"Interface '{Spec.Name}' has no event {opcode}.");
        }

        _client.WriteEvent(this, opcode, Spec.Events[(int)opcode].Wire, args);
    }

    public void PostError(uint code, string message) => _client.PostError(Id, code, message);

    public void Destroy()
    {
        if (_destroyed)
        {
            return;
        }

        _destroyed = true;
        _client.RemoveObject(this);
        _owner.OnTransportDestroyed(Handle);
    }

    internal override void DispatchRequest(uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args)
    {
        if (!_destroyed)
        {
            _owner.DispatchIncoming(opcode, args);
        }
    }
}
