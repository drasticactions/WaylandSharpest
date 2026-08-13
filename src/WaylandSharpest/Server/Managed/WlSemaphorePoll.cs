namespace Wayland.Server.Managed;

/// <summary>
/// The poll for hosts with no descriptors to watch. Every client there reports
/// its own readiness through a <see cref="WlTransportSignal"/>, which wakes this
/// wait, so there is nothing to register.
/// </summary>
internal sealed class WlSemaphorePoll : IWlPoll
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _wakeRequested;
    private volatile bool _disposed;

    public bool SupportsFds => false;

    public bool SupportsSignals => false;

    public int? PollableFd => null;

    public void AddFd(int fd, WlFdEvents events) => throw NotHere();

    public void ModFd(int fd, WlFdEvents events) => throw NotHere();

    public void RemoveFd(int fd)
    {
        // Nothing can have been registered.
    }

    public void AddSignal(int signalNumber) =>
        throw new PlatformNotSupportedException("This host cannot deliver signals to the event loop.");

    public void RemoveSignal(int signalNumber)
    {
    }

    public int Wait(Span<WlPollResult> results, int timeoutMs)
    {
        try
        {
            if (timeoutMs < 0)
            {
                _wake.Wait();
            }
            else
            {
                _wake.Wait(timeoutMs);
            }
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }

        Volatile.Write(ref _wakeRequested, 0);
        return 0;
    }

    public void Wake()
    {
        if (Interlocked.Exchange(ref _wakeRequested, 1) != 0 || _disposed)
        {
            return;
        }

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static PlatformNotSupportedException NotHere() =>
        new("This host cannot watch file descriptors; every client must supply an IWlClientTransport " +
            "that reports its own readiness.");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wake.Dispose();
    }
}
