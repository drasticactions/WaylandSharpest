namespace Wayland.Server;

/// <summary>
/// The readiness signal handed to a transport with no <see cref="IWlClientTransport.PollFd"/>.
/// </summary>
public sealed class WlTransportSignal
{
    private readonly Action<bool> _notify;

    internal WlTransportSignal(Action<bool> notify)
    {
        _notify = notify;
    }

    /// <summary>Inbound data or fd-slots are available to read.</summary>
    public void NotifyReadable() => _notify(false);

    /// <summary>The transport can accept writes again.</summary>
    public void NotifyWritable() => _notify(true);
}
