namespace Wayland.Server.Managed;

/// <summary>
/// The readiness primitive the event loop blocks on. Each platform has one, and
/// a host without one can still run clients that report their own readiness.
/// </summary>
internal interface IWlPoll : IDisposable
{
    /// <summary>Whether file descriptors can be watched at all.</summary>
    bool SupportsFds { get; }

    /// <summary>Whether signals can be delivered through the wait.</summary>
    bool SupportsSignals { get; }

    /// <summary>
    /// A descriptor that becomes readable when the wait would return, for
    /// hosts that drive this loop from a loop of their own, or null when there
    /// is none.
    /// </summary>
    int? PollableFd { get; }

    void AddFd(int fd, WlFdEvents events);

    void ModFd(int fd, WlFdEvents events);

    void RemoveFd(int fd);

    void AddSignal(int signalNumber);

    void RemoveSignal(int signalNumber);

    /// <summary>
    /// Waits for readiness. A negative timeout waits indefinitely, zero polls.
    /// Wakes are consumed rather than reported.
    /// </summary>
    int Wait(Span<WlPollResult> results, int timeoutMs);

    /// <summary>Interrupts a wait. Safe from any thread.</summary>
    void Wake();
}

/// <summary>Chooses the poll for the host.</summary>
internal static class WlPoll
{
    internal static IWlPoll CreatePlatformDefault()
    {
        if (OperatingSystem.IsLinux())
        {
            return new WlEpollPoll();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new WlKqueuePoll();
        }

        return new WlSemaphorePoll();
    }
}
