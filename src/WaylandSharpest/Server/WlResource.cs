using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// Base class for generated server-side protocol objects, wrapping a native
/// <c>wl_resource</c>. Incoming requests are delivered through a single
/// unmanaged dispatcher; generated subclasses decode arguments in
/// <see cref="HandleRequest"/> and expose them as C# events. Protocol events are
/// sent with <see cref="PostEvent"/>.
/// </summary>
public abstract unsafe class WlResource
{
    /// <summary>
    /// Resources created by this library, keyed by native pointer. Argument
    /// decoding resolves through this rather than through
    /// <c>wl_resource_get_user_data</c>, whose value may belong to another
    /// library sharing the display (e.g. wlroots).
    /// </summary>
    private static readonly ConcurrentDictionary<nint, WlResource> Owned = new();

    private nint _handle;
    private GCHandle _selfHandle;
    private bool _destroyed;

    /// <summary>Creates the native resource for a bound or requested protocol object id.</summary>
    protected WlResource(WlClient client, WlInterfaceSpec spec, uint version, uint id)
    {
        Client = client;
        var resource = LibWaylandServer.wl_resource_create(
            (wl_client*)client.RawHandle, spec.NativePointer, (int)version, id);
        if (resource == null)
        {
            throw new WaylandException($"wl_resource_create failed for '{spec.Name}' v{version} id {id}.");
        }

        _handle = (nint)resource;
        _selfHandle = GCHandle.Alloc(this);
        Owned[_handle] = this;
        var self = (void*)GCHandle.ToIntPtr(_selfHandle);
        LibWaylandServer.wl_resource_set_dispatcher(resource, &DispatchThunk, self, self, &DestroyThunk);
    }

    public WlClient Client { get; }

    public nint RawHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            return _handle;
        }
    }

    public uint Id => LibWaylandServer.wl_resource_get_id((wl_resource*)RawHandle);

    public uint Version => (uint)LibWaylandServer.wl_resource_get_version((wl_resource*)RawHandle);

    public bool IsDestroyed => _destroyed;

    /// <summary>Raised when the native resource is destroyed (client disconnect or destructor request).</summary>
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
            LibWaylandServer.wl_resource_destroy((wl_resource*)_handle);
        }
    }

    /// <summary>Posts a fatal protocol error to the client owning this resource.</summary>
    public void PostError(uint code, string message)
    {
        var format = Marshal.StringToCoTaskMemUTF8("%s");
        var text = Marshal.StringToCoTaskMemUTF8(message);
        try
        {
            LibWaylandServer.wl_resource_post_error_fixed(
                (wl_resource*)RawHandle, code, (sbyte*)format, (sbyte*)text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
            Marshal.FreeCoTaskMem(text);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DispatchThunk(void* implementation, void* target, uint opcode, wl_message* message, wl_argument* args)
    {
        var resource = (WlResource?)GCHandle.FromIntPtr((nint)implementation).Target;
        if (resource is null || resource._destroyed)
        {
            return 0;
        }

        try
        {
            var spec = resource.Spec;
            var argCount = opcode < spec.Requests.Count ? spec.Requests[(int)opcode].WireArgCount : 0;
            resource.HandleRequest(opcode, new ReadOnlySpan<WlArg>(args, argCount));
        }
        catch (Exception ex)
        {
            resource.Client.Display?.CaptureDispatchException(ex);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyThunk(wl_resource* resource)
    {
        var userData = (nint)LibWaylandServer.wl_resource_get_user_data(resource);
        if (userData == 0)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is WlResource managed)
        {
            managed.OnNativeDestroyed();
        }
    }

    private void OnNativeDestroyed()
    {
        _destroyed = true;
        Owned.TryRemove(_handle, out _);
        _handle = 0;
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

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
        fixed (WlArg* argsPtr = args)
        {
            LibWaylandServer.wl_resource_post_event_array(
                (wl_resource*)RawHandle, opcode, (wl_argument*)argsPtr);
        }
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
