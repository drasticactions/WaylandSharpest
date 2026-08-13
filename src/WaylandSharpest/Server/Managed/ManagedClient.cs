using System.Runtime.InteropServices;

namespace Wayland.Server.Managed;

/// <summary>
/// One connected client.
/// </summary>
internal sealed class ManagedClient : IWlClient, IWlWireHost
{
    private static long _nextHandle;

    private readonly ManagedDisplay _display;
    private readonly IWlClientTransport _transport;
    private readonly WlObjectTable _objects = new();
    private readonly WlWireReader _reader;
    private readonly WlWireWriter _writer;
    private readonly WlDisplayObject _displayObject;
    private readonly List<WlRegistryObject> _registries = [];
    private bool _tearingDown;
    private bool _destroyed;

    internal ManagedClient(ManagedDisplay display, IWlClientTransport transport)
    {
        _display = display;
        _transport = transport;
        RawHandle = (nint)Interlocked.Increment(ref _nextHandle);
        _reader = new WlWireReader(this, transport);
        _writer = new WlWireWriter(transport.CloseFd, transport.DuplicateFd, ResolveObjectId);
        _displayObject = new WlDisplayObject(this);
        _objects.Insert(_displayObject);
    }

    public nint RawHandle { get; }

    /// <summary>The wrapper generated code and compositors see.</summary>
    internal WlClient Owner { get; set; } = null!;

    internal ManagedDisplay Display => _display;

    internal IWlClientTransport Transport => _transport;

    internal WlObjectTable Objects => _objects;

    internal bool IsDestroyed => _destroyed;

    /// <summary>
    /// True once the connection is finished with but its teardown has not run.
    /// Requests still buffered are dropped rather than dispatched.
    /// </summary>
    internal bool IsDead { get; private set; }

    /// <summary>True while the writer is holding data the transport would not take.</summary>
    internal bool HasPendingWrite { get; set; }

    public IFdSlotTable? FdSlots => _transport.FdSlots;

    public uint MaxObjectId => _display.Options.MaxObjectId;

    internal WlWireReader Reader => _reader;

    /// <summary>Reads whatever the transport has and dispatches the whole messages in it.</summary>
    /// <returns>Whether anything was dispatched or the connection changed state.</returns>
    internal bool Pump()
    {
        if (IsDead)
        {
            return false;
        }

        var buffered = _reader.Data.Count;
        _reader.Fill();
        var progressed = _reader.Data.Count != buffered;

        if (HasPendingWrite && !_reader.IsFinished)
        {
            return progressed;
        }

        while (!IsDead)
        {
            bool dispatched;
            try
            {
                dispatched = _reader.TryDispatchOne();
            }
            catch (WlProtocolViolationException violation)
            {
                PostError(violation.ObjectId, violation.Code, violation.Message);
                return true;
            }

            if (!dispatched)
            {
                break;
            }

            progressed = true;
        }

        if (IsDead)
        {
            return true;
        }

        if (_reader.IsFloodingFds)
        {
            PostError(
                WlObjectIds.Display,
                WlDisplayError.InvalidMethod,
                "Too many file descriptors arrived without a message to carry them.");
            return true;
        }

        if (_reader.IsFinished)
        {
            Die();
            return true;
        }

        return progressed;
    }

    public WlWireSignature BeginRequest(uint objectId, uint opcode)
    {
        var target = _objects.Get(objectId)
            ?? throw new WlProtocolViolationException(
                WlObjectIds.Display,
                WlDisplayError.InvalidObject,
                $"No object with id {objectId}.");

        if (opcode >= (uint)target.Spec.Requests.Count)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidMethod,
                $"Interface '{target.Spec.Name}' has no request {opcode}.");
        }

        var signature = target.Spec.Requests[(int)opcode].Wire;
        if (signature.SinceVersion > target.Version)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidMethod,
                $"Request '{signature.Name}' arrived at version {target.Version} of " +
                $"'{target.Spec.Name}', which has it only since version {signature.SinceVersion}.");
        }

        return signature;
    }

    public bool TryResolveObject(uint id, out nint handle, out string interfaceName)
    {
        if (_objects.Get(id) is { } target)
        {
            handle = target.Handle;
            interfaceName = target.Spec.Name;
            return true;
        }

        handle = 0;
        interfaceName = string.Empty;
        return false;
    }

    public bool IsObjectIdInUse(uint id) => _objects.IsInUse(id);

    public void DispatchRequest(uint objectId, uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args)
    {
        var target = _objects.Get(objectId);
        if (target is null)
        {
            return;
        }

        _display.LogMessage(WlProtocolMessageDirection.Request, target, opcode, signature, args);

        try
        {
            target.DispatchRequest(opcode, signature, args);
        }
        catch (WlProtocolViolationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A compositor's handler failing is not the client's fault, so the
            // connection survives and the exception surfaces from dispatch.
            _display.Owner.CaptureDispatchException(ex);
        }
    }

    internal void WriteEvent(WlObject sender, uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args)
    {
        if (IsDead && sender != _displayObject)
        {
            return;
        }

        _display.LogMessage(WlProtocolMessageDirection.Event, sender, opcode, signature, args);
        _writer.WriteEvent(sender.Id, opcode, signature, args);

        if (_display.Options.MaxOutgoingBytes != 0 && _writer.BytesUsed > _display.Options.MaxOutgoingBytes)
        {
            // The client is not reading. Dropping it is the only way to stop
            // the queue growing without bound.
            Die();
        }
    }

    private uint ResolveObjectId(nint handle) =>
        _objects.GetByHandle(handle)?.Id
        ?? throw new WaylandException(
            "An event names an object that does not belong to this client, or that has been destroyed.");

    public void Flush()
    {
        if (_destroyed)
        {
            return;
        }

        HasPendingWrite = !TryFlush();
    }

    internal bool TryFlush()
    {
        try
        {
            return _writer.TryFlush(_transport);
        }
        catch (WaylandException)
        {
            Die();
            return true;
        }
    }

    internal void HandleSync(uint callbackId)
    {
        Span<WlArg> args = stackalloc WlArg[1];
        args[0].U = _display.NextSerial();

        // The callback is created and finished inside this one request, so it
        // never needs a place in the object table.
        var callback = new WlSyntheticObject(callbackId, WlCoreInterfaces.Callback);
        WriteEvent(callback, WlCoreInterfaces.CallbackDoneOpcode, WlCoreInterfaces.Callback.Events[0].Wire, args);
        SendDeleteId(callbackId);
    }

    /// <summary>Services wl_fixes.ack_global_remove for one registry.</summary>
    internal void AckGlobalRemove(nint registryHandle, uint globalName)
    {
        if (ResolveRegistry(registryHandle) is { } registry)
        {
            _display.SettleGlobal(registry, globalName);
        }
    }

    /// <summary>Services wl_fixes.destroy_registry.</summary>
    internal void DestroyRegistry(nint registryHandle)
    {
        if (ResolveRegistry(registryHandle) is not { } registry)
        {
            return;
        }

        _registries.Remove(registry);
        _display.SettleRegistry(registry);
        _objects.Remove(registry);
        SendDeleteId(registry.Id);
    }

    private WlRegistryObject? ResolveRegistry(nint handle) => _objects.GetByHandle(handle) as WlRegistryObject;

    internal void HandleGetRegistry(uint registryId)
    {
        var registry = new WlRegistryObject(this, registryId);
        _objects.Insert(registry);
        _registries.Add(registry);

        foreach (var global in _display.VisibleGlobals(Owner))
        {
            registry.SendGlobal(global);
        }
    }

    internal void HandleBind(WlRegistryObject registry, uint name, string? interfaceName, uint version, uint newId)
    {
        var global = _display.FindBindable(name, Owner)
            ?? throw new WlProtocolViolationException(
                registry.Id,
                WlDisplayError.InvalidObject,
                $"No global is advertised to this client under the name {name}.");

        if (interfaceName != global.Spec.Name)
        {
            throw new WlProtocolViolationException(
                registry.Id,
                WlDisplayError.InvalidObject,
                $"Global {name} is '{global.Spec.Name}' but the client asked to bind it as '{interfaceName}'.");
        }

        if (version == 0 || version > global.AdvertisedVersion)
        {
            throw new WlProtocolViolationException(
                registry.Id,
                WlDisplayError.InvalidObject,
                $"Global {name} is advertised at version {global.AdvertisedVersion}, and the client asked for {version}.");
        }

        global.Owner.HandleBind(Owner, version, newId);
    }

    /// <summary>Announces a global to every registry this client holds that may see it.</summary>
    internal void AnnounceGlobal(ManagedGlobal global)
    {
        foreach (var registry in _registries)
        {
            registry.SendGlobal(global);
        }
    }

    internal void AnnounceGlobalRemoval(ManagedGlobal global)
    {
        foreach (var registry in _registries)
        {
            registry.SendGlobalRemove(global);
        }
    }

    internal void PostError(uint objectId, uint code, string message)
    {
        if (IsDead)
        {
            return;
        }

        // The error has to fit inside one message, and the rest of it is a
        // header, an object id, a code and the string's own overhead.
        if (message.Length > 0xf80)
        {
            message = message[..0xf80];
        }

        var target = _objects.Get(objectId) ?? _displayObject;
        var text = Marshal.StringToCoTaskMemUTF8(message);
        try
        {
            Span<WlArg> args = stackalloc WlArg[3];
            args[0].Ptr = target.Handle;
            args[1].U = code;
            args[2].Ptr = text;
            WriteEvent(
                _displayObject,
                WlCoreInterfaces.DisplayErrorOpcode,
                WlCoreInterfaces.Display.Events[0].Wire,
                args);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }

        TryFlush();
        _transport.ShutdownRead();
        Die();
    }

    private void SendDeleteId(uint id)
    {
        if (_tearingDown || id >= WlObjectIds.ServerIdBase)
        {
            return;
        }

        Span<WlArg> args = stackalloc WlArg[1];
        args[0].U = id;
        WriteEvent(
            _displayObject,
            WlCoreInterfaces.DisplayDeleteIdOpcode,
            WlCoreInterfaces.Display.Events[1].Wire,
            args);
    }

    public IWlResource CreateResource(WlResource owner, WlInterfaceSpec spec, uint version, uint id)
    {
        spec.RealizeWires();
        var resource = new ManagedResource(this, owner, spec, version, id == 0 ? _objects.AllocateServerId() : id);
        _objects.Insert(resource);
        return resource;
    }

    internal void RemoveObject(WlObject target)
    {
        _objects.Remove(target);
        SendDeleteId(target.Id);
    }

    public nint GetObjectHandle(uint id) => _objects.Get(id)?.Handle ?? 0;

    public WlClientCredentials GetCredentials() => _transport.GetCredentials();

    public int Fd => _transport.PollFd
        ?? throw new NotSupportedException("This client has no connection file descriptor.");

    public void CloseFd(int fd) => _transport.CloseFd(fd);

    internal void Die()
    {
        if (IsDead)
        {
            return;
        }

        IsDead = true;
        _reader.Dispose();
        _display.EnqueueDeadClient(this);
    }

    public void Destroy() => Die();

    /// <summary>Releases everything the connection held. Runs once, outside dispatch.</summary>
    internal void TearDown()
    {
        if (_destroyed)
        {
            return;
        }

        _destroyed = true;
        _tearingDown = true;

        foreach (var registry in _registries)
        {
            _display.SettleRegistry(registry);
        }

        _registries.Clear();

        foreach (var target in _objects.Snapshot())
        {
            if (target is ManagedResource resource)
            {
                resource.Destroy();
            }
        }

        _writer.CloseUnsentFds();
        _reader.Dispose();
        _transport.Dispose();
        Owner.OnTransportDestroyed();
    }
}

/// <summary>
/// An object that exists only long enough to carry one event, such as the
/// callback a <c>sync</c> answers and immediately deletes.
/// </summary>
internal sealed class WlSyntheticObject : WlObject
{
    internal WlSyntheticObject(uint id, WlInterfaceSpec spec)
        : base(id, 1, spec)
    {
    }

    internal override void DispatchRequest(uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args)
    {
    }
}
