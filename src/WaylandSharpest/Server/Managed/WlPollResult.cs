namespace Wayland.Server.Managed;

/// <summary>One entry from <see cref="IWlPoll.Wait"/>.</summary>
internal readonly struct WlPollResult
{
    private WlPollResult(WlPollResultKind kind, int fd, int signal, WlFdEvents events)
    {
        Kind = kind;
        Fd = fd;
        Signal = signal;
        Events = events;
    }

    internal WlPollResultKind Kind { get; }

    internal int Fd { get; }

    internal int Signal { get; }

    internal WlFdEvents Events { get; }

    internal static WlPollResult ForFd(int fd, WlFdEvents events) =>
        new(WlPollResultKind.Fd, fd, 0, events);

    internal static WlPollResult ForSignal(int signalNumber) =>
        new(WlPollResultKind.Signal, -1, signalNumber, WlFdEvents.None);

    internal bool IsReadable => (Events & (WlFdEvents.Readable | WlFdEvents.Hangup | WlFdEvents.Error)) != 0;

    internal bool IsWritable => (Events & (WlFdEvents.Writable | WlFdEvents.Hangup | WlFdEvents.Error)) != 0;

    internal bool IsBroken => (Events & (WlFdEvents.Hangup | WlFdEvents.Error)) != 0;
}
