namespace Wayland.Server.Managed;

/// <summary>
/// The codes of <c>wl_display</c>'s <c>error</c> enum, used when the violation
/// is not attributable to a more specific interface.
/// </summary>
internal static class WlDisplayError
{
    /// <summary>The object id is not known to the server.</summary>
    internal const uint InvalidObject = 0;

    /// <summary>The object exists but the request does not, or is malformed.</summary>
    internal const uint InvalidMethod = 1;

    /// <summary>The server ran out of memory.</summary>
    internal const uint NoMemory = 2;

    /// <summary>The server hit a case it does not implement.</summary>
    internal const uint Implementation = 3;
}

/// <summary>
/// A client broke the protocol. Carrying the object id rather than the resource
/// keeps this usable from the wire codec, which runs before an object has
/// necessarily been resolved. An id of zero blames the display object itself.
/// </summary>
internal sealed class WlProtocolViolationException : Exception
{
    internal WlProtocolViolationException(uint objectId, uint code, string message)
        : base(message)
    {
        ObjectId = objectId;
        Code = code;
    }

    /// <summary>The offending object, or zero to blame the display.</summary>
    internal uint ObjectId { get; }

    /// <summary>The error code, interpreted against the offending object's interface.</summary>
    internal uint Code { get; }
}
