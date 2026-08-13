namespace Wayland.Server.Managed;

/// <summary>
/// The managed display's event loop: client dispatch, plus the descriptors,
/// timers, idle callbacks and signals a compositor hangs off it.
/// </summary>
internal sealed class WlManagedEventLoop : IWlEventLoop, IDisposable
{
    private readonly ManagedDisplay _display;
    private readonly IWlPoll _poll;
    private readonly Dictionary<int, WlManagedFdSource> _fdSources = [];
    private readonly Dictionary<int, ManagedClient> _clientsByFd = [];
    private readonly Dictionary<int, WlManagedSignalSource> _signalSources = [];
    private readonly List<WlManagedTimerSource> _timers = [];
    private readonly Queue<WlManagedIdleSource> _idle = new();
    private readonly WlPollResult[] _results = new WlPollResult[64];
    private bool _dispatching;
    private bool _disposed;

    internal WlManagedEventLoop(ManagedDisplay display)
    {
        _display = display;
        _poll = WlPoll.CreatePlatformDefault();
    }

    public nint RawHandle => 0;

    public int Fd => _poll.PollableFd
        ?? throw new NotSupportedException("This host's event loop has no pollable file descriptor.");

    internal void Wake() => _poll.Wake();

    /// <summary>Starts watching a client that has a descriptor to watch.</summary>
    internal void RegisterClient(ManagedClient client)
    {
        if (client.Transport.PollFd is not int fd)
        {
            return;
        }

        if (!_poll.SupportsFds)
        {
            throw new PlatformNotSupportedException(
                "This host cannot watch a client's connection file descriptor; supply an " +
                "IWlClientTransport that reports its own readiness instead.");
        }

        _clientsByFd[fd] = client;
        _poll.AddFd(fd, WlFdEvents.Readable);
    }

    internal void UnregisterClient(ManagedClient client)
    {
        if (client.Transport.PollFd is int fd && _clientsByFd.Remove(fd))
        {
            _poll.RemoveFd(fd);
        }
    }

    /// <summary>Matches what the loop watches for to whether the client can take more.</summary>
    internal void UpdateClientInterest(ManagedClient client)
    {
        if (client.Transport.PollFd is int fd && _clientsByFd.ContainsKey(fd))
        {
            _poll.ModFd(fd, client.HasPendingWrite ? WlFdEvents.Writable : WlFdEvents.Readable);
        }
    }

    public int Dispatch(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dispatching)
        {
            throw new InvalidOperationException(
                "This loop is already dispatching; a handler must not dispatch again from inside one.");
        }

        _dispatching = true;
        _display.IsDispatching = true;
        try
        {
            _display.DrainReadiness();
            var progressed = _display.DrainClients();
            _display.FlushClients();

            if (!progressed)
            {
                var count = _poll.Wait(_results, ClampToTimers(timeoutMs));
                ApplyResults(count);
                _display.DrainReadiness();
                _display.DrainClients();
                _display.FlushClients();
            }

            FireDueTimers();
            RunIdle();
        }
        finally
        {
            _dispatching = false;
            _display.IsDispatching = false;
        }

        _display.ReapDeadClients();
        return 0;
    }

    private void ApplyResults(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var result = _results[i];

            if (result.Kind == WlPollResultKind.Signal)
            {
                if (_signalSources.TryGetValue(result.Signal, out var signalSource))
                {
                    signalSource.Fire();
                }

                continue;
            }

            if (_clientsByFd.TryGetValue(result.Fd, out var client))
            {
                ApplyClientReadiness(client, result);
                continue;
            }

            if (_fdSources.TryGetValue(result.Fd, out var source))
            {
                source.Fire(result.Events);
            }
        }
    }

    private void ApplyClientReadiness(ManagedClient client, WlPollResult result)
    {
        if (result.IsReadable)
        {
            client.Reader.Readable = true;
        }

        if (result.IsWritable && client.HasPendingWrite && client.TryFlush())
        {
            client.HasPendingWrite = false;
            UpdateClientInterest(client);
        }

        if (result.IsBroken)
        {
            // Clearing back-pressure lets the drain select this client and take
            // it through the ordinary disconnect, rather than skipping it for
            // ever while it spins.
            client.HasPendingWrite = false;
            client.Reader.Readable = true;
        }
    }

    public IWlEventSource AddFd(int fd, WlFdEvents events, Action<int, WlFdEvents> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!_poll.SupportsFds)
        {
            throw new PlatformNotSupportedException("This host's event loop cannot watch file descriptors.");
        }

        if (_fdSources.ContainsKey(fd) || _clientsByFd.ContainsKey(fd))
        {
            throw new InvalidOperationException($"File descriptor {fd} is already watched by this loop.");
        }

        var source = new WlManagedFdSource(this, fd, callback);
        _fdSources[fd] = source;
        _poll.AddFd(fd, events);
        return source;
    }

    public IWlEventSource AddTimer(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var source = new WlManagedTimerSource(this, callback);
        _timers.Add(source);
        return source;
    }

    public IWlEventSource AddIdle(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var source = new WlManagedIdleSource(callback);
        _idle.Enqueue(source);
        _poll.Wake();
        return source;
    }

    public IWlEventSource AddSignal(int signalNumber, Action<int> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!_poll.SupportsSignals)
        {
            throw new PlatformNotSupportedException("This host's event loop cannot deliver signals.");
        }

        if (_signalSources.ContainsKey(signalNumber))
        {
            throw new InvalidOperationException($"Signal {signalNumber} is already handled by this loop.");
        }

        var source = new WlManagedSignalSource(this, signalNumber, callback);
        _signalSources[signalNumber] = source;
        _poll.AddSignal(signalNumber);
        return source;
    }

    public void DispatchIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RunIdle();
    }

    internal void RemoveFdSource(WlManagedFdSource source)
    {
        if (_fdSources.Remove(source.Fd))
        {
            _poll.RemoveFd(source.Fd);
        }
    }

    internal void UpdateFdSource(WlManagedFdSource source, WlFdEvents events)
    {
        if (_fdSources.ContainsKey(source.Fd))
        {
            _poll.ModFd(source.Fd, events);
        }
    }

    internal void RemoveTimerSource(WlManagedTimerSource source) => _timers.Remove(source);

    internal void RemoveSignalSource(WlManagedSignalSource source)
    {
        if (_signalSources.Remove(source.SignalNumber))
        {
            _poll.RemoveSignal(source.SignalNumber);
        }
    }

    internal void RunHandler(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            _display.Owner.CaptureDispatchException(ex);
        }
    }

    /// <summary>
    /// Shortens the wait so that a timer due before it would expire still fires
    /// on time.
    /// </summary>
    private int ClampToTimers(int timeoutMs)
    {
        var soonest = long.MaxValue;
        foreach (var timer in _timers)
        {
            if (timer.Deadline is { } deadline && deadline < soonest)
            {
                soonest = deadline;
            }
        }

        if (soonest == long.MaxValue)
        {
            return _idle.Count > 0 ? 0 : timeoutMs;
        }

        var remaining = (int)Math.Clamp(soonest - Environment.TickCount64, 0, int.MaxValue);
        return timeoutMs < 0 ? remaining : Math.Min(timeoutMs, remaining);
    }

    private void FireDueTimers()
    {
        var now = Environment.TickCount64;
        for (var i = _timers.Count - 1; i >= 0; i--)
        {
            var timer = _timers[i];
            if (timer.Deadline is { } deadline && deadline <= now)
            {
                timer.Fire();
            }
        }
    }

    private void RunIdle()
    {
        while (_idle.Count > 0)
        {
            var source = _idle.Dequeue();
            source.Fire(this);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fdSources.Clear();
        _clientsByFd.Clear();
        _signalSources.Clear();
        _timers.Clear();
        _idle.Clear();
        _poll.Dispose();
    }
}

/// <summary>A source that only supports what its kind can do.</summary>
internal abstract class WlManagedSource : IWlEventSource
{
    public bool IsRemoved { get; protected set; }

    public abstract void Remove();

    public virtual void UpdateTimer(int delayMs) =>
        throw new NotSupportedException("This event source is not a timer.");

    public virtual void UpdateFd(WlFdEvents events) =>
        throw new NotSupportedException("This event source does not watch a file descriptor.");
}

internal sealed class WlManagedFdSource(WlManagedEventLoop loop, int fd, Action<int, WlFdEvents> callback)
    : WlManagedSource
{
    internal int Fd => fd;

    internal void Fire(WlFdEvents events)
    {
        if (!IsRemoved)
        {
            loop.RunHandler(() => callback(fd, events));
        }
    }

    public override void Remove()
    {
        if (!IsRemoved)
        {
            IsRemoved = true;
            loop.RemoveFdSource(this);
        }
    }

    public override void UpdateFd(WlFdEvents events)
    {
        ObjectDisposedException.ThrowIf(IsRemoved, this);
        loop.UpdateFdSource(this, events);
    }
}

/// <summary>
/// A timer fires once per arming, so a repeating timer arms itself again from
/// its own callback.
/// </summary>
internal sealed class WlManagedTimerSource(WlManagedEventLoop loop, Action callback) : WlManagedSource
{
    internal long? Deadline { get; private set; }

    internal void Fire()
    {
        Deadline = null;
        if (!IsRemoved)
        {
            loop.RunHandler(callback);
        }
    }

    public override void Remove()
    {
        if (!IsRemoved)
        {
            IsRemoved = true;
            Deadline = null;
            loop.RemoveTimerSource(this);
        }
    }

    public override void UpdateTimer(int delayMs)
    {
        ObjectDisposedException.ThrowIf(IsRemoved, this);
        Deadline = delayMs <= 0 ? null : Environment.TickCount64 + delayMs;
    }
}

/// <summary>Runs once, before the loop next waits.</summary>
internal sealed class WlManagedIdleSource(Action callback) : WlManagedSource
{
    internal void Fire(WlManagedEventLoop loop)
    {
        if (IsRemoved)
        {
            return;
        }

        IsRemoved = true;
        loop.RunHandler(callback);
    }

    public override void Remove() => IsRemoved = true;
}

internal sealed class WlManagedSignalSource(WlManagedEventLoop loop, int signalNumber, Action<int> callback)
    : WlManagedSource
{
    internal int SignalNumber => signalNumber;

    internal void Fire()
    {
        if (!IsRemoved)
        {
            loop.RunHandler(() => callback(signalNumber));
        }
    }

    public override void Remove()
    {
        if (!IsRemoved)
        {
            IsRemoved = true;
            loop.RemoveSignalSource(this);
        }
    }
}
