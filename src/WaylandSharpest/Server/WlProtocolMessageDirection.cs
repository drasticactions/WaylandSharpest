namespace Wayland.Server;

/// <summary>Which way a logged protocol message was travelling.</summary>
public enum WlProtocolMessageDirection
{
    /// <summary>Client to server.</summary>
    Request,

    /// <summary>Server to client.</summary>
    Event,
}
