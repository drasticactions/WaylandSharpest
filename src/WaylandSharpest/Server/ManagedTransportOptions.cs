namespace Wayland.Server;

/// <summary>
/// Bounds on what one client may make the server allocate.
/// </summary>
public sealed record ManagedTransportOptions
{
    /// <summary>
    /// The highest object id a client may allocate, or zero for no limit.
    /// Without one, a client can grow the server's object table indefinitely.
    /// </summary>
    public uint MaxObjectId { get; init; } = 1_000_000;

    /// <summary>
    /// How many bytes of events may queue for one client before it is
    /// disconnected, or zero for no limit. Without one, a client that stops
    /// reading while the compositor keeps sending grows the queue indefinitely.
    /// </summary>
    public int MaxOutgoingBytes { get; init; } = 16 * 1024 * 1024;
}
