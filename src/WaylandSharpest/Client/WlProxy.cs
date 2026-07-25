using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland;

/// <summary>
/// Base class for all generated client-side protocol objects, wrapping a native
/// <c>wl_proxy</c>. Event delivery uses a single unmanaged dispatcher installed
/// via <c>wl_proxy_add_dispatcher</c>; generated subclasses decode arguments in
/// <see cref="HandleEvent"/>.
/// </summary>
public abstract unsafe class WlProxy : IDisposable
{
    internal const uint MarshalFlagDestroy = 1; // WL_MARSHAL_FLAG_DESTROY

    private nint _handle;
    private GCHandle _selfHandle;
    private bool _destroyed;
    private bool _borrowed;

    protected WlProxy(nint handle, WlDisplay? display)
    {
        if (handle == 0)
        {
            throw new WaylandException($"Cannot create {GetType().Name} from a null proxy handle.");
        }

        _handle = handle;
        Display = display ?? (WlDisplay)this;
    }

    /// <summary>The connection this object belongs to.</summary>
    public WlDisplay Display { get; }

    /// <summary>The native <c>wl_proxy*</c> handle.</summary>
    public nint RawHandle
    {
        get
        {
            ThrowIfDestroyed();
            return _handle;
        }
    }

    /// <summary>The protocol object id.</summary>
    public uint Id => LibWaylandClient.wl_proxy_get_id((wl_proxy*)RawHandle);

    /// <summary>The negotiated interface version of this object instance.</summary>
    public uint Version => LibWaylandClient.wl_proxy_get_version((wl_proxy*)RawHandle);

    /// <summary>True once the object has been destroyed (via a destructor request or <see cref="Dispose"/>).</summary>
    public bool IsDestroyed => _destroyed;

    /// <summary>
    /// True when the native proxy is owned by native code (e.g. a buffer owned
    /// by a wayland-cursor theme). <see cref="Dispose"/> is a no-op on borrowed
    /// objects; the native owner invalidates them when it goes away.
    /// </summary>
    public bool IsBorrowed => _borrowed;

    /// <summary>Interface metadata; implemented by generated classes.</summary>
    protected abstract WlInterfaceSpec Spec { get; }

    /// <summary>Decodes and raises the event for <paramref name="opcode"/>; implemented by generated classes.</summary>
    protected abstract void HandleEvent(uint opcode, ReadOnlySpan<WlArg> args);

    /// <summary>
    /// Destroys the protocol object. Generated classes route this through the
    /// interface's destructor request when it has one.
    /// </summary>
    public void Dispose()
    {
        if (_destroyed || _borrowed)
        {
            return;
        }

        DisposeCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>Default teardown: plain <c>wl_proxy_destroy</c> without a destructor request.</summary>
    protected virtual void DisposeCore()
    {
        LibWaylandClient.wl_proxy_destroy((wl_proxy*)_handle);
        MarkDestroyed();
    }

    /// <summary>Marks the managed wrapper dead and releases its GC handle.</summary>
    protected void MarkDestroyed()
    {
        _destroyed = true;
        _handle = 0;
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private void ThrowIfDestroyed()
    {
        if (_destroyed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    // ---- Request marshalling (called by generated code) ----

    /// <summary>Sends a request that creates no object.</summary>
    protected void MarshalRequest(uint opcode, scoped ReadOnlySpan<WlArg> args) =>
        MarshalCore(opcode, args, null, Version, 0);

    /// <summary>Sends a destructor request; the native proxy is destroyed atomically.</summary>
    protected void MarshalDestructor(uint opcode, scoped ReadOnlySpan<WlArg> args)
    {
        MarshalCore(opcode, args, null, Version, MarshalFlagDestroy);
        MarkDestroyed();
    }

    /// <summary>Sends a request with a typed <c>new_id</c> argument and wraps the created proxy.</summary>
    protected WlProxy MarshalConstructor(uint opcode, scoped ReadOnlySpan<WlArg> args, WlInterfaceSpec iface) =>
        MarshalCore(opcode, args, iface, Version, 0)!;

    /// <summary>
    /// Sends a request whose <c>new_id</c> argument carries no compile-time
    /// interface (only <c>wl_registry.bind</c> in practice); the interface name
    /// and version travel as explicit arguments.
    /// </summary>
    protected WlProxy MarshalBind(uint opcode, scoped ReadOnlySpan<WlArg> args, WlInterfaceSpec iface, uint version) =>
        MarshalCore(opcode, args, iface, version, 0)!;

    private WlProxy? MarshalCore(uint opcode, scoped ReadOnlySpan<WlArg> args, WlInterfaceSpec? iface, uint version, uint flags)
    {
        ThrowIfDestroyed();
        fixed (WlArg* argsPtr = args)
        {
            var result = LibWaylandClient.wl_proxy_marshal_array_flags(
                (wl_proxy*)_handle,
                opcode,
                iface is null ? null : iface.NativePointer,
                version,
                flags,
                (wl_argument*)argsPtr);

            if (iface is null)
            {
                return null;
            }

            if (result == null)
            {
                throw new WaylandException($"{Spec.Name}@{Id}: request opcode {opcode} failed to create a '{iface.Name}' object.");
            }

            return CreateWrapped(iface, (nint)result, Display);
        }
    }

    /// <summary>Wraps a native proxy in its managed class and installs the event dispatcher.</summary>
    internal static WlProxy CreateWrapped(WlInterfaceSpec iface, nint handle, WlDisplay display)
    {
        var proxy = iface.CreateProxy(handle, display);
        proxy.AttachDispatcher();
        return proxy;
    }

    /// <summary>
    /// Wraps a native proxy whose lifetime belongs to native code. The wrapper
    /// behaves normally (requests, events) but never destroys the proxy; the
    /// owner must call <see cref="ReleaseBorrowed"/> before destroying it.
    /// </summary>
    internal static WlProxy CreateBorrowed(WlInterfaceSpec iface, nint handle, WlDisplay display)
    {
        var proxy = CreateWrapped(iface, handle, display);
        proxy._borrowed = true;
        return proxy;
    }

    /// <summary>Invalidates a borrowed wrapper just before its native owner destroys the proxy.</summary>
    internal void ReleaseBorrowed() => MarkDestroyed();

    private void AttachDispatcher()
    {
        _selfHandle = GCHandle.Alloc(this);
        var self = (void*)GCHandle.ToIntPtr(_selfHandle);
        // 'implementation' is handed back as the dispatcher's first parameter;
        // 'data' becomes wl_proxy user data, which object-argument decoding uses.
        LibWaylandClient.wl_proxy_add_dispatcher((wl_proxy*)_handle, &DispatchThunk, self, self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DispatchThunk(void* implementation, void* target, uint opcode, wl_message* message, wl_argument* args)
    {
        var proxy = (WlProxy?)GCHandle.FromIntPtr((nint)implementation).Target;
        if (proxy is null || proxy._destroyed)
        {
            return 0;
        }

        try
        {
            var argCount = opcode < proxy.Spec.Events.Count ? proxy.Spec.Events[(int)opcode].WireArgCount : 0;
            proxy.HandleEvent(opcode, new ReadOnlySpan<WlArg>(args, argCount));
        }
        catch (Exception ex)
        {
            // Exceptions must not cross the unmanaged frame; the display rethrows
            // after the dispatch call that triggered this event returns.
            proxy.Display.CaptureDispatchException(ex);
        }

        return 0;
    }

    // ---- Event argument decoding (called by generated code) ----

    /// <summary>Decodes an object argument via its proxy's user data.</summary>
    protected static T? GetProxy<T>(WlArg arg) where T : WlProxy
    {
        if (arg.Ptr == 0)
        {
            return null;
        }

        var userData = (nint)LibWaylandClient.wl_proxy_get_user_data((wl_proxy*)arg.Ptr);
        if (userData == 0)
        {
            return null;
        }

        return GCHandle.FromIntPtr(userData).Target as T;
    }

    /// <summary>Wraps the server-created proxy of a <c>new_id</c> event argument.</summary>
    protected WlProxy WrapNewProxy(WlArg arg, WlInterfaceSpec iface) =>
        CreateWrapped(iface, arg.Ptr, Display);

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

    // ---- Request argument building (called by generated code) ----

    /// <summary>Allocates a native UTF-8 copy of <paramref name="value"/> for a string argument.</summary>
    protected static nint AllocString(string? value) =>
        value is null ? 0 : Marshal.StringToCoTaskMemUTF8(value);

    protected static void FreeString(nint ptr)
    {
        if (ptr != 0)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>
    /// Allocates a native <c>wl_array</c> holding a copy of <paramref name="data"/>
    /// for the duration of a request. Dispose after marshalling.
    /// </summary>
    protected static WlNativeArray AllocArray(ReadOnlySpan<byte> data) => WlNativeArray.Create(data);

    public override string ToString() => _destroyed ? $"{Spec.Name}(destroyed)" : $"{Spec.Name}@{Id}";
}
