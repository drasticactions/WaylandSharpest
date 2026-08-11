namespace Wayland.Server;

/// <summary>
/// Token table behind an fd-less transport.
/// </summary>
public interface IFdSlotTable
{
    /// <summary>Mints a new slot referencing <paramref name="payload"/>.</summary>
    int Mint(object payload);

    /// <summary>
    /// The payload behind <paramref name="slot"/>. Throws when the slot is
    /// unknown or the payload is not a <typeparamref name="T"/>.
    /// </summary>
    T Resolve<T>(int slot) where T : class;

    /// <summary>Mints a new slot on the same payload (<c>dup(2)</c>).</summary>
    int Duplicate(int slot);

    /// <summary>
    /// Releases the slot. Closing an unknown slot is harmless, mirroring
    /// <c>close(2)</c> on a stale fd.
    /// </summary>
    void Close(int slot);
}

/// <summary>
/// Reference counting for payloads whose lifetime follows their slots.
/// </summary>
public interface IFdSlotPayload
{
    /// <summary>Takes a reference.</summary>
    void AddRef();

    /// <summary>Drops a reference; the payload frees itself at zero.</summary>
    void Release();
}

/// <summary>The default <see cref="IFdSlotTable"/> implementation.</summary>
public sealed class FdSlotTable : IFdSlotTable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, object> _slots = [];
    private int _next = 1;

    /// <summary>Live slots, for teardown assertions.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _slots.Count;
            }
        }
    }

    /// <inheritdoc/>
    public int Mint(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_lock)
        {
            var slot = _next++;
            (payload as IFdSlotPayload)?.AddRef();
            _slots.Add(slot, payload);
            return slot;
        }
    }

    /// <inheritdoc/>
    public T Resolve<T>(int slot) where T : class
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slot, out var payload))
            {
                throw new WaylandException($"Unknown fd-slot {slot}.");
            }

            return payload as T
                ?? throw new WaylandException($"fd-slot {slot} is a {payload.GetType().Name}, expected {typeof(T).Name}.");
        }
    }

    /// <inheritdoc/>
    public int Duplicate(int slot)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(slot, out var payload))
            {
                throw new WaylandException($"Unknown fd-slot {slot}.");
            }

            var dup = _next++;
            (payload as IFdSlotPayload)?.AddRef();
            _slots.Add(dup, payload);
            return dup;
        }
    }

    /// <inheritdoc/>
    public void Close(int slot)
    {
        object? payload;
        lock (_lock)
        {
            if (!_slots.Remove(slot, out payload))
            {
                return;
            }
        }

        (payload as IFdSlotPayload)?.Release();
    }
}
