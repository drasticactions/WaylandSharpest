using Wayland.Server.Managed.Interop;
using static Wayland.Server.Managed.Interop.MacOSPoll;

namespace Wayland.Server.Managed;

/// <summary>
/// The macOS poll. kqueue registers interest per descriptor and filter rather
/// than per descriptor and mask, so switching between reading and writing
/// changes two registrations at once instead of replacing one.
/// </summary>
internal sealed unsafe class WlKqueuePoll : IWlPoll
{
    private const int MaxEvents = 64;

    private readonly int _kqueueFd;
    private readonly HashSet<int> _signals = [];
    private readonly object _wakeLock = new();
    private int _wakeRequested;
    private bool _disposed;

    internal WlKqueuePoll()
    {
        _kqueueFd = kqueue();
        if (_kqueueFd < 0)
        {
            throw Libc.Failure("kqueue");
        }

        Libc.SetCloseOnExec(_kqueueFd);

        // The wake channel is a queue entry rather than a descriptor, so it
        // needs no pipe and no read to drain. EV_CLEAR resets it once reported.
        var wake = KEvent.For(WakeIdent, EVFILT_USER, EV_ADD | EV_CLEAR);
        if (Apply(&wake, 1) < 0)
        {
            Libc.close(_kqueueFd);
            throw Libc.Failure("kevent(EVFILT_USER)");
        }
    }

    public bool SupportsFds => true;

    public bool SupportsSignals => true;

    public int? PollableFd => _kqueueFd;

    public void AddFd(int fd, WlFdEvents events)
    {
        var changes = stackalloc KEvent[2];
        changes[0] = KEvent.For((nuint)fd, EVFILT_READ, (ushort)(EV_ADD | Enablement(events, WlFdEvents.Readable)));
        changes[1] = KEvent.For((nuint)fd, EVFILT_WRITE, (ushort)(EV_ADD | Enablement(events, WlFdEvents.Writable)));
        if (Apply(changes, 2) < 0)
        {
            throw Libc.Failure("kevent(EV_ADD)");
        }
    }

    public void ModFd(int fd, WlFdEvents events) => AddFd(fd, events);

    public void RemoveFd(int fd)
    {
        var changes = stackalloc KEvent[2];
        changes[0] = KEvent.For((nuint)fd, EVFILT_READ, EV_DELETE);
        changes[1] = KEvent.For((nuint)fd, EVFILT_WRITE, EV_DELETE);

        // A filter that was never registered reports ENOENT, which is not a
        // failure worth surfacing during teardown.
        Apply(changes, 2);
    }

    public void AddSignal(int signalNumber)
    {
        if (!_signals.Add(signalNumber))
        {
            return;
        }

        // The queue reports a signal in addition to the process's own handling
        // of it, so the default action has to be stood down or the process
        // still dies before the loop ever sees it.
        SetSignalDisposition(signalNumber, SIG_IGN);

        var change = KEvent.For((nuint)signalNumber, EVFILT_SIGNAL, EV_ADD);
        if (Apply(&change, 1) < 0)
        {
            _signals.Remove(signalNumber);
            throw Libc.Failure("kevent(EVFILT_SIGNAL)");
        }
    }

    public void RemoveSignal(int signalNumber)
    {
        if (_signals.Remove(signalNumber))
        {
            var change = KEvent.For((nuint)signalNumber, EVFILT_SIGNAL, EV_DELETE);
            Apply(&change, 1);
        }
    }

    public int Wait(Span<WlPollResult> results, int timeoutMs)
    {
        var maxEvents = Math.Min(results.Length, MaxEvents);
        var events = stackalloc KEvent[maxEvents];

        int count;
        if (timeoutMs < 0)
        {
            count = kevent(_kqueueFd, null, 0, events, maxEvents, null);
        }
        else
        {
            var timeout = TimeSpec.FromMilliseconds(timeoutMs);
            count = kevent(_kqueueFd, null, 0, events, maxEvents, &timeout);
        }

        if (count < 0)
        {
            if (Libc.Errno == Libc.EINTR)
            {
                return 0;
            }

            throw Libc.Failure("kevent");
        }

        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = events[i];

            if (entry.Filter == EVFILT_USER)
            {
                Volatile.Write(ref _wakeRequested, 0);
                continue;
            }

            if (entry.Filter == EVFILT_SIGNAL)
            {
                results[written++] = WlPollResult.ForSignal((int)entry.Ident);
                continue;
            }

            var ready = entry.Filter == EVFILT_READ ? WlFdEvents.Readable : WlFdEvents.Writable;

            // Hangup and failure arrive as flags on the filter that noticed
            // them, not as filters of their own.
            if ((entry.Flags & EV_EOF) != 0)
            {
                ready |= WlFdEvents.Hangup;
            }

            if ((entry.Flags & EV_ERROR) != 0)
            {
                ready |= WlFdEvents.Error;
            }

            results[written++] = WlPollResult.ForFd((int)entry.Ident, ready);
        }

        return written;
    }

    public void Wake()
    {
        if (Interlocked.Exchange(ref _wakeRequested, 1) != 0)
        {
            return;
        }

        lock (_wakeLock)
        {
            if (_disposed)
            {
                return;
            }

            var change = KEvent.For(WakeIdent, EVFILT_USER, 0, NOTE_TRIGGER);
            Apply(&change, 1);
        }
    }

    private static ushort Enablement(WlFdEvents events, WlFdEvents wanted) =>
        (events & wanted) != 0 ? EV_ENABLE : EV_DISABLE;

    private int Apply(KEvent* changes, int count) =>
        kevent(_kqueueFd, changes, count, null, 0, null);

    public void Dispose()
    {
        lock (_wakeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _signals.Clear();
        Libc.close(_kqueueFd);
    }
}
