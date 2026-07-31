using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// Base class for generated server-side protocol objects.
/// </summary>
public abstract unsafe class WlResource
{
    /// <summary>
    /// Resources created by this library, keyed by transport handle.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, WlResource> Owned = new();

    private readonly IWlResource _impl;
    private bool _destroyed;

    /// <summary>Creates the transport resource for a bound or requested protocol object id.</summary>
    protected WlResource(WlClient client, WlInterfaceSpec spec, uint version, uint id)
    {
        Client = client;
        _impl = client.Impl.CreateResource(this, spec, version, id);
        Owned[_impl.RawHandle] = this;
    }

    public WlClient Client { get; }

    public nint RawHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            return _impl.RawHandle;
        }
    }

    public uint Id
    {
        get
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            return _impl.Id;
        }
    }

    public uint Version
    {
        get
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            return _impl.Version;
        }
    }

    public bool IsDestroyed => _destroyed;

    /// <summary>Raised when the underlying resource is destroyed (client disconnect or destructor request).</summary>
    public event EventHandler? Destroyed;

    /// <summary>Interface metadata; implemented by generated classes.</summary>
    protected abstract WlInterfaceSpec Spec { get; }

    /// <summary>Decodes and raises the C# event for the request <paramref name="opcode"/>; implemented by generated classes.</summary>
    protected abstract void HandleRequest(uint opcode, ReadOnlySpan<WlArg> args);

    /// <summary>Destroys the resource, notifying the client.</summary>
    public void Destroy()
    {
        if (!_destroyed)
        {
            _impl.Destroy();
        }
    }

    /// <summary>Posts a fatal protocol error to the client owning this resource.</summary>
    public void PostError(uint code, string message)
    {
        ObjectDisposedException.ThrowIf(_destroyed, this);
        _impl.PostError(code, message);
    }

    /// <summary>Delivers an incoming request from the transport. May throw; the transport captures.</summary>
    internal void DispatchIncoming(uint opcode, ReadOnlySpan<WlArg> args)
    {
        if (!_destroyed)
        {
            HandleRequest(opcode, args);
        }
    }

    /// <summary>Called by the transport when the underlying resource has been destroyed.</summary>
    internal void OnTransportDestroyed(nint rawHandle)
    {
        _destroyed = true;
        Owned.TryRemove(rawHandle, out _);
        try
        {
            Destroyed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Client.Display?.CaptureDispatchException(ex);
        }
    }

    /// <summary>
    /// Called by generated code after raising the event for a destructor-flagged
    /// request; the protocol requires the server to destroy the resource.
    /// </summary>
    protected void CompleteDestructorRequest() => Destroy();

    // ---- Event sending (called by generated code) ----

    protected void PostEvent(uint opcode, scoped ReadOnlySpan<WlArg> args)
    {
        ObjectDisposedException.ThrowIf(_destroyed, this);
        _impl.PostEvent(opcode, args);
    }

    // ---- Request argument decoding (called by generated code) ----

    /// <summary>
    /// Decodes an object argument. Returns <c>null</c> for resources this
    /// library did not create rather than reinterpreting their user
    /// data. Use <see cref="GetResourceHandle"/> to reach those.
    /// </summary>
    protected static T? GetResource<T>(WlArg arg) where T : WlResource
    {
        if (arg.Ptr == 0)
        {
            return null;
        }

        return Owned.TryGetValue(arg.Ptr, out var resource) ? resource as T : null;
    }

    /// <summary>
    /// The raw <c>wl_resource*</c> of an object argument, regardless of owner.
    /// Use to bridge to a native library that owns the resource, e.g.
    /// <c>wlr_surface_from_resource</c>. Returns 0 for a null argument.
    /// </summary>
    protected static nint GetResourceHandle(WlArg arg) => arg.Ptr;

    protected static string? GetString(WlArg arg) => Marshal.PtrToStringUTF8(arg.Ptr);

    protected static byte[] GetArray(WlArg arg)
    {
        if (arg.Ptr == 0)
        {
            return [];
        }

        var array = (wl_array*)arg.Ptr;
        var result = new byte[(int)array->size];
        new ReadOnlySpan<byte>(array->data, result.Length).CopyTo(result);
        return result;
    }

    // ---- Event argument building (called by generated code) ----

    protected static nint AllocString(string? value) =>
        value is null ? 0 : Marshal.StringToCoTaskMemUTF8(value);

    /// <summary>
    /// Allocates a native <c>wl_array</c> holding a copy of <paramref name="data"/>
    /// for the duration of an event post. Dispose after posting.
    /// </summary>
    protected static WlNativeArray AllocArray(ReadOnlySpan<byte> data) => WlNativeArray.Create(data);

    protected static void FreeString(nint ptr)
    {
        if (ptr != 0)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    public override string ToString() => _destroyed ? $"{Spec.Name}(destroyed)" : $"{Spec.Name}@{Id}";
}
