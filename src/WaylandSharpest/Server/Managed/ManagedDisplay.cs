using System.Runtime.InteropServices;

namespace Wayland.Server.Managed;

/// <summary>
/// The managed display: its clients, its globals, and the dispatch that drives
/// them.
/// </summary>
internal sealed class ManagedDisplay : IWlDisplay
{
    private readonly WlServerDisplay _owner;
    private readonly List<ManagedClient> _clients = [];
    private readonly List<ManagedGlobal> _globals = [];
    private readonly List<ManagedClient> _deadClients = [];
    private readonly List<WlProtocolLogger> _loggers = [];
    private readonly object _readinessLock = new();
    private readonly Queue<(ManagedClient Client, bool Writable)> _readiness = new();
    private uint _nextGlobalName = 1;
    private uint _serial;
    private bool _dispatching;
    private bool _disposed;

    internal ManagedDisplay(WlServerDisplay owner, ManagedTransportOptions options)
    {
        _owner = owner;
        Options = options;
        Loop = new WlManagedEventLoop(this);
    }

    internal WlServerDisplay Owner => _owner;

    internal ManagedTransportOptions Options { get; }

    internal WlManagedEventLoop Loop { get; }

    internal bool IsTerminated { get; private set; }

    public nint RawHandle => 0;

    public IWlEventLoop EventLoop => Loop;

    public WlClient CreateClient(int fd) => CreateClient(new WlClientTransport(fd));

    public WlClient CreateClient(IWlClientTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var impl = new ManagedClient(this, transport);
        var client = new WlClient(impl, _owner);
        impl.Owner = client;
        _clients.Add(impl);

        try
        {
            Loop.RegisterClient(impl);
        }
        catch
        {
            _clients.Remove(impl);
            throw;
        }

        transport.SetSignal(new WlTransportSignal(writable => SignalReadiness(impl, writable)));

        try
        {
            _clientCreated?.Invoke(client);
        }
        catch (Exception ex)
        {
            _owner.CaptureDispatchException(ex);
        }

        return client;
    }

    public IReadOnlyList<WlClient> GetClients() => [.. _clients.Select(static c => c.Owner)];

    private Action<WlClient>? _clientCreated;

    public Action<WlClient>? ClientCreatedHandler
    {
        get => _clientCreated;
        set => _clientCreated = value;
    }

    internal void EnqueueDeadClient(ManagedClient client)
    {
        if (!_deadClients.Contains(client))
        {
            _deadClients.Add(client);
        }

        if (!_dispatching)
        {
            ReapDeadClients();
        }
    }

    internal void ReapDeadClients()
    {
        while (_deadClients.Count > 0)
        {
            var client = _deadClients[0];
            _deadClients.RemoveAt(0);
            _clients.Remove(client);
            Loop.UnregisterClient(client);
            client.TearDown();
        }
    }

    private void SignalReadiness(ManagedClient client, bool writable)
    {
        lock (_readinessLock)
        {
            if (_disposed)
            {
                return;
            }

            _readiness.Enqueue((client, writable));
        }

        Loop.Wake();
    }

    /// <summary>Moves readiness reported from other threads onto the dispatch thread.</summary>
    internal int DrainReadiness()
    {
        var drained = 0;
        while (true)
        {
            (ManagedClient Client, bool Writable) entry;
            lock (_readinessLock)
            {
                if (_readiness.Count == 0)
                {
                    return drained;
                }

                entry = _readiness.Dequeue();
            }

            if (entry.Client.IsDestroyed || entry.Client.IsDead)
            {
                continue;
            }

            if (entry.Writable)
            {
                if (entry.Client.HasPendingWrite && entry.Client.TryFlush())
                {
                    entry.Client.HasPendingWrite = false;
                }
            }
            else
            {
                entry.Client.Reader.Readable = true;
            }

            drained++;
        }
    }

    /// <summary>Set by the loop for as long as handlers may be running.</summary>
    internal bool IsDispatching
    {
        get => _dispatching;
        set => _dispatching = value;
    }

    /// <summary>
    /// Reads from every client that has something waiting and dispatches it.
    /// </summary>
    /// <returns>Whether anything was read or dispatched.</returns>
    internal bool DrainClients()
    {
        var progressed = false;
        for (var i = 0; i < _clients.Count; i++)
        {
            var client = _clients[i];
            if (client.IsDead || client.IsDestroyed)
            {
                continue;
            }

            progressed |= client.Pump();
        }

        return progressed;
    }

    public void Run()
    {
        while (!IsTerminated && !_disposed)
        {
            Loop.Dispatch(-1);
        }
    }

    public void Terminate()
    {
        IsTerminated = true;
        Loop.Wake();
    }

    public void FlushClients()
    {
        for (var i = 0; i < _clients.Count; i++)
        {
            var client = _clients[i];
            if (client.IsDestroyed)
            {
                continue;
            }

            var wasPending = client.HasPendingWrite;
            client.HasPendingWrite = !client.TryFlush();
            if (client.HasPendingWrite != wasPending)
            {
                Loop.UpdateClientInterest(client);
            }
        }
    }

    public uint NextSerial() => ++_serial;

    public IWlGlobal CreateGlobal(WlGlobal owner, WlInterfaceSpec iface, int version)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (version > iface.Version)
        {
            throw new WaylandException(
                $"Cannot publish '{iface.Name}' at version {version}; the protocol defines up to {iface.Version}.");
        }

        var global = new ManagedGlobal(this, owner, iface, _nextGlobalName++, (uint)version);
        _globals.Add(global);

        foreach (var client in _clients.ToArray())
        {
            if (!client.IsDead && IsVisibleTo(global, client.Owner))
            {
                client.AnnounceGlobal(global);
            }
        }

        return global;
    }

    internal void AnnounceRemoval(ManagedGlobal global)
    {
        foreach (var client in _clients.ToArray())
        {
            if (!client.IsDead)
            {
                client.AnnounceGlobalRemoval(global);
            }
        }
    }

    internal void RemoveGlobal(ManagedGlobal global)
    {
        if (!global.IsRemoved)
        {
            AnnounceRemoval(global);
        }

        _globals.Remove(global);
    }

    public bool SupportsFixes => true;

    public void AckGlobalRemove(WlClient client, nint fixesHandle, nint registryHandle, uint globalName) =>
        Impl(client).AckGlobalRemove(registryHandle, globalName);

    public void DestroyRegistry(WlClient client, nint registryHandle) =>
        Impl(client).DestroyRegistry(registryHandle);

    private static ManagedClient Impl(WlClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.Impl as ManagedClient
            ?? throw new WaylandException("This client is not served by the managed transport.");
    }

    /// <summary>Records that one registry acknowledged one global's removal.</summary>
    internal void SettleGlobal(WlRegistryObject registry, uint globalName)
    {
        for (var i = 0; i < _globals.Count; i++)
        {
            if (_globals[i].Name == globalName)
            {
                _globals[i].Settled(registry);
                return;
            }
        }
    }

    internal void SettleRegistry(WlRegistryObject registry)
    {
        foreach (var global in _globals.ToArray())
        {
            global.Settled(registry);
        }
    }

    internal void RunWithdrawn(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            _owner.CaptureDispatchException(ex);
        }
    }

    /// <summary>The globals a client's registry should be told about.</summary>
    internal IEnumerable<ManagedGlobal> VisibleGlobals(WlClient client) =>
        _globals.Where(global => !global.IsRemoved && IsVisibleTo(global, client)).ToArray();

    /// <summary>
    /// The global a client may bind under a name. A removed global still
    /// resolves, because the client's request and the removal cross on the wire.
    /// </summary>
    internal ManagedGlobal? FindBindable(uint name, WlClient client) =>
        _globals.FirstOrDefault(global => global.Name == name && IsVisibleTo(global, client));

    internal bool IsVisibleTo(ManagedGlobal global, WlClient client)
    {
        if (_globalFilter is not { } filter)
        {
            return true;
        }

        try
        {
            return filter(client, global.Owner, global.Spec.Name);
        }
        catch (Exception ex)
        {
            _owner.CaptureDispatchException(ex);
            return false;
        }
    }

    private WlServerDisplay.GlobalFilter? _globalFilter;

    public void SetGlobalFilter(WlServerDisplay.GlobalFilter? filter) => _globalFilter = filter;

    private readonly List<WlListeningSocket> _sockets = [];

    public bool SupportsLocalSocket => !OperatingSystem.IsWindows();

    public string AddSocketAuto()
    {
        var socket = WlListeningSocket.BindAuto();
        Listen(socket);
        return socket.Name!;
    }

    public void AddSocket(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Listen(WlListeningSocket.Bind(name));
    }

    public void AddSocketFd(int fd) => Listen(WlListeningSocket.Adopt(fd));

    private void Listen(WlListeningSocket socket)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _sockets.Add(socket);
            Loop.AddFd(socket.Fd, WlFdEvents.Readable, (_, _) => AcceptPending(socket));
        }
        catch
        {
            _sockets.Remove(socket);
            socket.Dispose();
            throw;
        }
    }

    private void AcceptPending(WlListeningSocket socket)
    {
        while (true)
        {
            var fd = socket.TryAccept();
            if (fd < 0)
            {
                return;
            }

            try
            {
                CreateClient(fd);
            }
            catch (Exception ex)
            {
                _owner.CaptureDispatchException(ex);
                return;
            }
        }
    }

    public IDisposable AddProtocolLogger(WlProtocolLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loggers.Add(logger);
        return new LoggerRegistration(this, logger);
    }

    internal void LogMessage(
        WlProtocolMessageDirection direction,
        WlObject target,
        uint opcode,
        WlWireSignature signature,
        ReadOnlySpan<WlArg> args)
    {
        if (_loggers.Count == 0)
        {
            return;
        }

        var messages = direction == WlProtocolMessageDirection.Request ? target.Spec.Requests : target.Spec.Events;
        var message = new WlProtocolMessage(
            direction,
            target.Handle,
            target.Id,
            target.Spec.Name,
            signature.Name,
            messages[(int)opcode].Signature,
            (int)opcode,
            args);

        foreach (var logger in _loggers.ToArray())
        {
            try
            {
                logger(in message);
            }
            catch (Exception ex)
            {
                _owner.CaptureDispatchException(ex);
            }
        }
    }

    private sealed class LoggerRegistration(ManagedDisplay display, WlProtocolLogger logger) : IDisposable
    {
        private bool _removed;

        public void Dispose()
        {
            if (!_removed)
            {
                _removed = true;
                display._loggers.Remove(logger);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var client in _clients.ToArray())
        {
            client.TearDown();
        }

        _clients.Clear();
        _deadClients.Clear();
        _globals.Clear();

        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }

        _sockets.Clear();
        Loop.Dispose();
    }
}
