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
