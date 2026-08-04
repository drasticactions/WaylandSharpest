using System.Collections.Concurrent;
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

    private static readonly ConcurrentDictionary<nint, WlProxy> Owned = new();

    private nint _handle;
    private GCHandle _selfHandle;
    private bool _destroyed;
    private bool _borrowed;
    private bool _isWrapper;
    private WlEventQueue? _queue;

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

    /// <summary>
    /// True when this is a queue wrapper rather than a real object: a second
    /// handle used to send requests on the wrapped object's behalf. A wrapper
    /// receives no events and destroying it does not destroy the object.
    /// </summary>
    public bool IsWrapper => _isWrapper;

    /// <summary>
    /// The queue this object's events are delivered on; <c>null</c> means the
    /// display's default queue. Objects created by a request inherit their
    /// creator's queue at creation time.
    /// </summary>
    public WlEventQueue? Queue => _queue;

    /// <summary>
    /// Moves this object's events to <paramref name="queue"/>; <c>null</c>
    /// restores the default queue. Racy on an object that may already have
    /// events in flight — prefer creating the object through a wrapper
    /// (<see cref="CreateWrapper{T}"/>) so it is born on the right queue.
    /// </summary>
    public void SetQueue(WlEventQueue? queue)
    {
        ThrowIfDestroyed();
        LibWaylandClient.wl_proxy_set_queue(
            (wl_proxy*)_handle, queue is null ? null : (wl_event_queue*)queue.RawHandle);
        TrackQueue(queue);
    }

    /// <summary>
    /// Creates a queue wrapper for this object: a second handle that sends
    /// requests on this object's behalf, where objects created through it are
    /// born on the wrapper's queue instead of racing between creation and
    /// <see cref="SetQueue"/>. A wrapper receives no events. Dispose it when
    /// done; that does not destroy the underlying object.
    /// </summary>
    /// <remarks>
    /// Do not send destructor requests through a wrapper: the destroy flag would
    /// target the wrapper rather than the object.
    /// </remarks>
    public T CreateWrapper<T>(WlEventQueue? queue) where T : WlProxy
    {
        ThrowIfDestroyed();
        var wrapper = LibWaylandClient.wl_proxy_create_wrapper((void*)_handle);
        if (wrapper == null)
        {
            throw new WaylandException($"wl_proxy_create_wrapper failed for {Spec.Name}.");
        }

        // A wrapper is never registered in Owned and never gets a dispatcher:
        // wl_proxy_add_dispatcher aborts the process on one.
        var proxy = Spec.CreateProxy((nint)wrapper, Display);
        proxy._isWrapper = true;
        if (queue is not null)
        {
            LibWaylandClient.wl_proxy_set_queue((wl_proxy*)wrapper, (wl_event_queue*)queue.RawHandle);
        }

        // A wrapper receives no events but still points at the queue, and
        // objects it creates are born there, so it counts against disposal.
        proxy.TrackQueue(queue);
        return (T)proxy;
    }

    /// <summary>
    /// Reads the effective queue back from libwayland and updates the managed
    /// accounting. A proxy created by a request inherits its parent's queue
    /// inside libwayland without any managed call, so the count has to be
    /// derived rather than tracked.
    /// </summary>
    private void SyncQueueFromNative() =>
        TrackQueue(WlEventQueue.FromHandle((nint)LibWaylandClient.wl_proxy_get_queue((wl_proxy*)_handle)));

    private void TrackQueue(WlEventQueue? queue)
    {
        if (ReferenceEquals(_queue, queue))
        {
            return;
        }

        _queue?.Detach();
        _queue = queue;
        queue?.Attach();
    }

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

        // The wrapper check must come first: generated classes override
        // DisposeCore to send the interface's destructor request, which on a
        // wrapper would destroy the real object and then abort the process in
        // wl_proxy_destroy.
        if (_isWrapper)
        {
            LibWaylandClient.wl_proxy_wrapper_destroy((void*)_handle);
            MarkDestroyed();
            GC.SuppressFinalize(this);
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
        Owned.TryRemove(_handle, out _);
        _handle = 0;
        _queue?.Detach();
        _queue = null;
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
    /// Sends a destructor request that also creates an object: the new proxy is
    /// returned and this proxy is destroyed atomically.
    /// </summary>
    protected WlProxy MarshalDestructorConstructor(uint opcode, scoped ReadOnlySpan<WlArg> args, WlInterfaceSpec iface)
    {
        // MarshalCore reads the handle and rejects a destroyed proxy, so the
        // managed bookkeeping has to follow it.
        var created = MarshalCore(opcode, args, iface, Version, MarshalFlagDestroy)!;
        MarkDestroyed();
        return created;
    }

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

    /// <summary>
    /// Wraps a native proxy in its managed class, registers it for
    /// object-argument decoding, and installs the event dispatcher. This is the
    /// single path for real proxies; wrappers deliberately skip all three.
    /// </summary>
    internal static WlProxy CreateWrapped(WlInterfaceSpec iface, nint handle, WlDisplay display)
    {
        var proxy = iface.CreateProxy(handle, display);
        proxy.Register();
        proxy.AttachDispatcher();
        return proxy;
    }

    /// <summary>
    /// Publishes the proxy for object-argument decoding and records the queue
    /// libwayland gave it.
    /// </summary>
    internal void Register()
    {
        Owned[_handle] = this;
        SyncQueueFromNative();
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
            // Exceptions must not cross the unmanaged frame; they are rethrown
            // after the dispatch call that triggered this event returns. Route
            // by queue so a render thread's handler does not surface on the main
            // thread mid-unrelated-call.
            if (proxy._queue is { } queue)
            {
                queue.CaptureDispatchException(ex);
            }
            else
            {
                proxy.Display.CaptureDispatchException(ex);
            }
        }

        return 0;
    }

    // ---- Event argument decoding (called by generated code) ----

    /// <summary>
    /// Decodes an object argument. Returns <c>null</c> for proxies this library
    /// did not create rather than reinterpreting their user data. Use
    /// <see cref="GetProxyHandle"/> to reach those.
    /// </summary>
    protected static T? GetProxy<T>(WlArg arg) where T : WlProxy
    {
        if (arg.Ptr == 0)
        {
            return null;
        }

        return Owned.TryGetValue(arg.Ptr, out var proxy) ? proxy as T : null;
    }

    /// <summary>
    /// The raw <c>wl_proxy*</c> of an object argument, regardless of owner. Use
    /// to bridge to a native library that owns the proxy. Returns 0 for a null
    /// argument.
    /// </summary>
    protected static nint GetProxyHandle(WlArg arg) => arg.Ptr;

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
