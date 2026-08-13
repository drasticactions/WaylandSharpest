namespace Wayland.Server.Managed;

/// <summary>
/// The object model a <see cref="WlWireReader"/> decodes against. Implemented by
/// the client, and by test doubles that need framing without a display.
/// </summary>
internal interface IWlWireHost
{
    /// <summary>
    /// The signature of the request <paramref name="opcode"/> on the object
    /// <paramref name="objectId"/>.
    /// </summary>
    /// <exception cref="WlProtocolViolationException">
    /// The object does not exist, has no such request, or was created at a
    /// version older than the request requires.
    /// </exception>
    WlWireSignature BeginRequest(uint objectId, uint opcode);

    /// <summary>
    /// Resolves an object argument. Returns false when no object holds
    /// <paramref name="id"/>.
    /// </summary>
    bool TryResolveObject(uint id, out nint handle, out string interfaceName);

    /// <summary>Whether <paramref name="id"/> already names a live object.</summary>
    bool IsObjectIdInUse(uint id);

    /// <summary>
    /// The highest object id a client may allocate, or zero for no limit.
    /// Bounds how much memory one client's object table can occupy.
    /// </summary>
    uint MaxObjectId { get; }

    /// <summary>Delivers a decoded request. The argument storage dies with the call.</summary>
    void DispatchRequest(uint objectId, uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args);

    /// <summary>Releases an fd-slot the reader could not hand on.</summary>
    void CloseFd(int fd);
}
