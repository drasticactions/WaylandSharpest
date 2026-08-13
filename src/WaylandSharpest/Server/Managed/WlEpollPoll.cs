using Wayland.Server.Managed.Interop;
using static Wayland.Server.Managed.Interop.LinuxPoll;

namespace Wayland.Server.Managed;

/// <summary>The Linux poll: epoll for readiness, an eventfd to interrupt it.</summary>
internal sealed unsafe class WlEpollPoll : IWlPoll
{
    private const int MaxEvents = 64;

    private readonly int _epollFd;
    private readonly int _eventFd;
    private readonly Dictionary<int, int> _signalFds = [];
    private readonly object _wakeLock = new();
    private int _wakeRequested;
    private bool _disposed;

    internal WlEpollPoll()
    {
        _epollFd = epoll_create1(EPOLL_CLOEXEC);
        if (_epollFd < 0)
        {
            throw Libc.Failure("epoll_create1");
        }

        _eventFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
        if (_eventFd < 0)
        {
            Libc.close(_epollFd);
            throw Libc.Failure("eventfd");
        }

        try
        {
            Control(EPOLL_CTL_ADD, _eventFd, EPOLLIN);
        }
        catch
        {
            Libc.close(_eventFd);
            Libc.close(_epollFd);
            throw;
        }
    }

    public bool SupportsFds => true;

    public bool SupportsSignals => true;

    public int? PollableFd => _epollFd;

    public void AddFd(int fd, WlFdEvents events) => Control(EPOLL_CTL_ADD, fd, ToEpoll(events));

    public void ModFd(int fd, WlFdEvents events) => Control(EPOLL_CTL_MOD, fd, ToEpoll(events));

    public void RemoveFd(int fd) => epoll_ctl(_epollFd, EPOLL_CTL_DEL, fd, null);

    public void AddSignal(int signalNumber)
    {
        if (_signalFds.ContainsKey(signalNumber))
        {
            return;
        }

        // The descriptor only sees a signal that the thread is not taking the
        // default action on, so it has to be blocked first.
        Span<byte> mask = stackalloc byte[SigSetSize];
        FillSigSet(mask, signalNumber);

        fixed (byte* maskPtr = mask)
        {
            if (pthread_sigmask(SIG_BLOCK, maskPtr, null) != 0)
            {
                throw Libc.Failure("pthread_sigmask");
            }

            var fd = signalfd(-1, maskPtr, SFD_NONBLOCK | SFD_CLOEXEC);
            if (fd < 0)
            {
                throw Libc.Failure("signalfd");
            }

            _signalFds[signalNumber] = fd;
            Control(EPOLL_CTL_ADD, fd, EPOLLIN);
        }
    }

    public void RemoveSignal(int signalNumber)
    {
        if (_signalFds.Remove(signalNumber, out var fd))
        {
            epoll_ctl(_epollFd, EPOLL_CTL_DEL, fd, null);
            Libc.close(fd);
        }
    }

    public int Wait(Span<WlPollResult> results, int timeoutMs)
    {
        var maxEvents = Math.Min(results.Length, MaxEvents);
        Span<byte> buffer = stackalloc byte[maxEvents * EventSize];

        int count;
        fixed (byte* bufferPtr = buffer)
        {
            count = epoll_wait(_epollFd, bufferPtr, maxEvents, timeoutMs);
        }

        if (count < 0)
        {
            if (Libc.Errno == Libc.EINTR)
            {
                return 0;
            }

            throw Libc.Failure("epoll_wait");
        }

        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var (events, fd) = ReadEvent(buffer, i);

            if (fd == _eventFd)
            {
                DrainWake();
                continue;
            }

            var signalNumber = SignalFor(fd);
            if (signalNumber != 0)
            {
                if (TryReadSignal(fd))
                {
                    results[written++] = WlPollResult.ForSignal(signalNumber);
                }

                continue;
            }

            results[written++] = WlPollResult.ForFd(fd, FromEpoll(events));
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

            ulong one = 1;
            Libc.write(_eventFd, &one, 8);
        }
    }

    private void DrainWake()
    {
        ulong value;
        Libc.read(_eventFd, &value, 8);
        Volatile.Write(ref _wakeRequested, 0);
    }

    private int SignalFor(int fd)
    {
        foreach (var (signalNumber, signalFd) in _signalFds)
        {
            if (signalFd == fd)
            {
                return signalNumber;
            }
        }

        return 0;
    }

    private static bool TryReadSignal(int fd)
    {
        Span<byte> info = stackalloc byte[SigInfoSize];
        fixed (byte* infoPtr = info)
        {
            return Libc.read(fd, infoPtr, SigInfoSize) == SigInfoSize;
        }
    }

    private void Control(int operation, int fd, uint events)
    {
        Span<byte> buffer = stackalloc byte[EventSize];
        WriteEvent(buffer, 0, events, fd);
        fixed (byte* bufferPtr = buffer)
        {
            if (epoll_ctl(_epollFd, operation, fd, bufferPtr) < 0)
            {
                throw Libc.Failure($"epoll_ctl({operation})");
            }
        }
    }

    private static uint ToEpoll(WlFdEvents events)
    {
        uint result = 0;
        if ((events & WlFdEvents.Readable) != 0)
        {
            result |= EPOLLIN;
        }

        if ((events & WlFdEvents.Writable) != 0)
        {
            result |= EPOLLOUT;
        }

        return result;
    }

    private static WlFdEvents FromEpoll(uint events)
    {
        var result = WlFdEvents.None;
        if ((events & EPOLLIN) != 0)
        {
            result |= WlFdEvents.Readable;
        }

        if ((events & EPOLLOUT) != 0)
        {
            result |= WlFdEvents.Writable;
        }

        if ((events & EPOLLHUP) != 0)
        {
            result |= WlFdEvents.Hangup;
        }

        if ((events & EPOLLERR) != 0)
        {
            result |= WlFdEvents.Error;
        }

        return result;
    }

    public void Dispose()
    {
        lock (_wakeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Libc.close(_eventFd);
        }

        foreach (var fd in _signalFds.Values)
        {
            Libc.close(fd);
        }

        _signalFds.Clear();
        Libc.close(_epollFd);
    }
}
