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
