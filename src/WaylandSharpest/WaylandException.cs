namespace Wayland;

/// <summary>Base exception for Wayland binding errors.</summary>
public class WaylandException : Exception
{
    public WaylandException(string message) : base(message)
    {
    }

    public WaylandException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Thrown when the compositor reported a fatal protocol error for the connection
/// (<c>wl_display_get_error</c> / <c>wl_display_get_protocol_error</c>).
/// </summary>
public sealed class WaylandProtocolException : WaylandException
{
    public WaylandProtocolException(string message, int errorCode, string? interfaceName, uint objectId)
        : base(message)
    {
        ErrorCode = errorCode;
        InterfaceName = interfaceName;
        ObjectId = objectId;
    }

    /// <summary>The interface-specific protocol error code.</summary>
    public int ErrorCode { get; }

    /// <summary>Name of the interface that generated the error, if known.</summary>
    public string? InterfaceName { get; }

    /// <summary>Id of the object that generated the error, if known.</summary>
    public uint ObjectId { get; }
}

/// <summary>
/// Thrown when a request or event is used on an object whose negotiated version
/// predates it. Sending it anyway would be a wire violation libwayland does not
/// catch: the peer fails to decode the message and the connection dies with no
/// indication of the real cause.
/// </summary>
public sealed class WaylandVersionException : WaylandException
{
    public WaylandVersionException(string interfaceName, string messageName, uint requiredVersion, uint actualVersion)
        : base($"'{interfaceName}.{messageName}' requires interface version {requiredVersion}, but this object negotiated version {actualVersion}.")
    {
        InterfaceName = interfaceName;
        MessageName = messageName;
        RequiredVersion = requiredVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>The interface the message belongs to.</summary>
    public string InterfaceName { get; }

    /// <summary>The request or event that was used.</summary>
    public string MessageName { get; }

    /// <summary>The interface version the message was introduced in.</summary>
    public uint RequiredVersion { get; }

    /// <summary>The version this object actually negotiated.</summary>
    public uint ActualVersion { get; }
}
