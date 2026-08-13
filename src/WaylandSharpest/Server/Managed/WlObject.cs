namespace Wayland.Server.Managed;

/// <summary>
/// Something a client can address by object id. Most are
/// <see cref="ManagedResource"/>s standing behind a compositor's
/// <see cref="WlResource"/>; the display and registry are served by the
/// connection itself.
/// </summary>
internal abstract class WlObject
{
    private static long _nextHandle;

    protected WlObject(uint id, uint version, WlInterfaceSpec spec)
    {
        Id = id;
        Version = version;
        Spec = spec;

        // Handles are never reused, so one that outlives its object resolves to
        // nothing rather than to whatever took its place.
        Handle = (nint)Interlocked.Increment(ref _nextHandle);
    }

    internal uint Id { get; }

    internal uint Version { get; }

    internal WlInterfaceSpec Spec { get; }

    /// <summary>The value this object takes in an argument, unique for its lifetime.</summary>
    internal nint Handle { get; }

    /// <summary>Handles one request addressed to this object.</summary>
    internal abstract void DispatchRequest(uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args);
}

/// <summary>
/// A client's object ids. A client allocates from the low range and the server
/// from the high one, so the two ends never collide and neither has to ask.
/// </summary>
internal sealed class WlObjectTable
{
    private const int InitialServerCapacity = 16;

    private readonly Dictionary<uint, WlObject> _clientObjects = [];
    private readonly Dictionary<nint, WlObject> _byHandle = [];
    private WlObject?[] _serverObjects = new WlObject?[InitialServerCapacity];
    private readonly Stack<uint> _freeServerIds = new();
    private uint _nextServerId = WlObjectIds.ServerIdBase;

    /// <summary>Live objects, for teardown assertions.</summary>
    internal int Count => _byHandle.Count;

    internal WlObject? Get(uint id)
    {
        if (id == 0)
        {
            return null;
        }

        if (id >= WlObjectIds.ServerIdBase)
        {
            var index = id - WlObjectIds.ServerIdBase;
            return index < (uint)_serverObjects.Length ? _serverObjects[index] : null;
        }

        return _clientObjects.GetValueOrDefault(id);
    }

    internal WlObject? GetByHandle(nint handle) => _byHandle.GetValueOrDefault(handle);

    /// <summary>Every live object, safe to mutate the table while walking.</summary>
    internal WlObject[] Snapshot() => [.. _byHandle.Values];

    internal void Insert(WlObject wlObject)
    {
        var id = wlObject.Id;
        if (id >= WlObjectIds.ServerIdBase)
        {
            var index = id - WlObjectIds.ServerIdBase;
            if (index >= (uint)_serverObjects.Length)
            {
                Array.Resize(ref _serverObjects, (int)Math.Max(index + 1, (uint)_serverObjects.Length * 2));
            }

            _serverObjects[index] = wlObject;
        }
        else
        {
            if (id == 0 || id > WlObjectIds.ClientIdMax)
            {
                throw new WaylandException($"Object id {id} is outside the range a client may allocate.");
            }

            if (!_clientObjects.TryAdd(id, wlObject))
            {
                throw new WaylandException($"Object id {id} is already in use.");
            }
        }

        _byHandle[wlObject.Handle] = wlObject;
    }

    /// <summary>Takes the next unused server id.</summary>
    internal uint AllocateServerId()
    {
        if (_freeServerIds.TryPop(out var reused))
        {
            return reused;
        }

        if (_nextServerId == uint.MaxValue)
        {
            throw new WaylandException("The server has run out of object ids for this client.");
        }

        return _nextServerId++;
    }

    internal void Remove(WlObject wlObject)
    {
        var id = wlObject.Id;
        if (id >= WlObjectIds.ServerIdBase)
        {
            var index = id - WlObjectIds.ServerIdBase;
            if (index < (uint)_serverObjects.Length && ReferenceEquals(_serverObjects[index], wlObject))
            {
                _serverObjects[index] = null;
                _freeServerIds.Push(id);
            }
        }
        else if (_clientObjects.TryGetValue(id, out var existing) && ReferenceEquals(existing, wlObject))
        {
            _clientObjects.Remove(id);
        }

        _byHandle.Remove(wlObject.Handle);
    }

    /// <summary>
    /// Whether an id names a live object. Dense id packing is not enforced: a
    /// client may legitimately use an id the server has just deleted, because
    /// the deletion and the request cross on the wire.
    /// </summary>
    internal bool IsInUse(uint id) => Get(id) is not null;
}
