using Wayland.Server.Managed;

namespace Wayland.Server;

/// <summary>
/// A transport that speaks the Wayland wire protocol itself instead of handing
/// it to <c>libwayland-server</c>. It carries no version floor and no native
/// dependency beyond the socket layer, so a display can run where libwayland
/// does not.
/// </summary>
/// <example>
/// <code>
/// using var display = WlServerDisplay.Create(new ManagedTransport());
/// </code>
/// </example>
public sealed class ManagedTransport : IWlServerTransport
{
    private readonly ManagedTransportOptions _options;

    /// <summary>Creates a transport with the default resource limits.</summary>
    public ManagedTransport()
        : this(new ManagedTransportOptions())
    {
    }

    /// <summary>Creates a transport with explicit resource limits.</summary>
    public ManagedTransport(ManagedTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public IWlDisplay CreateDisplay(WlServerDisplay owner) => new ManagedDisplay(owner, _options);
}

/// <summary>
/// Bounds on what one client may make the server allocate. The defaults are
/// generous enough that a well-behaved client never meets them, and low enough
/// that a hostile one cannot exhaust the server.
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

    /// <summary>
    /// Whether the display may listen on a local socket under
    /// <c>$XDG_RUNTIME_DIR</c>. Off by default where the host has no such
    /// socket, and a display without one still accepts clients on transports
    /// the compositor supplies.
    /// </summary>
    public bool LocalSocket { get; init; } = !OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser();
}
