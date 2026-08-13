namespace Wayland.Server.Managed;

/// <summary>
/// A global the display advertises. Removal and destruction are separate: a
/// removed global stops being advertised but still resolves, so a client whose
/// bind crossed the removal on the wire is answered rather than killed.
/// </summary>
internal sealed class ManagedGlobal : IWlGlobal
{
    private readonly ManagedDisplay _display;
    private readonly HashSet<WlRegistryObject> _awaitingAcknowledgement = [];
    private Action? _withdrawn;
    private bool _removed;
    private bool _disposed;

    internal ManagedGlobal(ManagedDisplay display, WlGlobal owner, WlInterfaceSpec spec, uint name, uint version)
    {
        _display = display;
        Owner = owner;
        Spec = spec;
        Name = name;
        AdvertisedVersion = version;
    }

    internal WlGlobal Owner { get; }

    internal WlInterfaceSpec Spec { get; }

    /// <summary>The registry name clients bind by.</summary>
    internal uint Name { get; }

    /// <summary>The version this global was published at, readable after disposal.</summary>
    internal uint AdvertisedVersion { get; }

    public uint Version
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return AdvertisedVersion;
        }
    }

    internal bool IsRemoved => _removed;

    internal bool IsDisposed => _disposed;

    public bool SupportsWithdrawn => true;

    public Action? WithdrawnHandler
    {
        get => _withdrawn;
        set => _withdrawn = value;
    }

    public uint NameFor(WlClient client)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _display.IsVisibleTo(this, client) ? Name : 0;
    }

    public void Remove()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_removed)
        {
            throw new InvalidOperationException(
                $"The '{Spec.Name}' global has already been removed.");
        }

        _removed = true;
        _display.AnnounceRemoval(this);
        CheckWithdrawn();
    }

    /// <summary>Records that a registry was told about this global.</summary>
    internal void Announced(WlRegistryObject registry) => _awaitingAcknowledgement.Add(registry);

    /// <summary>
    /// Records that a registry can no longer bind this global, either because
    /// the client acknowledged the removal or because the registry is gone.
    /// </summary>
    internal void Settled(WlRegistryObject registry)
    {
        if (_awaitingAcknowledgement.Remove(registry) && _removed)
        {
            CheckWithdrawn();
        }
    }

    private void CheckWithdrawn()
    {
        if (_awaitingAcknowledgement.Count != 0 || _withdrawn is null)
        {
            return;
        }

        var handler = _withdrawn;
        _withdrawn = null;
        _display.RunWithdrawn(handler);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _withdrawn = null;
        _display.RemoveGlobal(this);
    }
}
