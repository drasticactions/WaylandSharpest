using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// The default <see cref="IWlServerTransport"/>, running the wire protocol on
/// <c>libwayland-server</c>.
/// </summary>
public sealed class LibWaylandTransport : IWlServerTransport
{
    private LibWaylandTransport()
    {
    }

    public static LibWaylandTransport Instance { get; } = new();

    public IWlDisplay CreateDisplay(WlServerDisplay owner) => new LibWaylandDisplay(owner);
}

internal sealed unsafe class LibWaylandDisplay : IWlDisplay
{
    /// <summary>
    /// Client wrappers interned per native pointer so bind and resource
    /// callbacks observe stable identity. Entries are removed by the client
    /// destroy listener, so a natural disconnect cannot leave a stale wrapper
    /// behind for a later client allocated at the same address.
    /// </summary>
    private readonly ConcurrentDictionary<nint, WlClient> _clients = new();
    private readonly WlServerDisplay _owner;
    private nint _handle;

    internal LibWaylandDisplay(WlServerDisplay owner)
    {
        _owner = owner;
        var display = LibWaylandServer.wl_display_create();
        if (display == null)
        {
            throw new WaylandException("wl_display_create failed.");
        }

        _handle = (nint)display;
        EventLoop = new LibWaylandEventLoop((nint)LibWaylandServer.wl_display_get_event_loop(display));
    }

    public nint RawHandle => _handle;

    public IWlEventLoop EventLoop { get; }

    internal WlServerDisplay Owner => _owner;

    public string AddSocketAuto()
    {
        var name = LibWaylandServer.wl_display_add_socket_auto((wl_display*)_handle);
        if (name == null)
        {
            throw new WaylandException("wl_display_add_socket_auto failed.");
        }

        return Marshal.PtrToStringUTF8((nint)name)!;
    }

    public void AddSocket(string name)
    {
        var namePtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            if (LibWaylandServer.wl_display_add_socket((wl_display*)_handle, (sbyte*)namePtr) != 0)
            {
                throw new WaylandException($"wl_display_add_socket('{name}') failed.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    public void AddSocketFd(int fd)
    {
        if (LibWaylandServer.wl_display_add_socket_fd((wl_display*)_handle, fd) != 0)
        {
            throw new WaylandException($"wl_display_add_socket_fd({fd}) failed.");
        }
    }

    public WlClient CreateClient(int fd)
    {
        var client = LibWaylandServer.wl_client_create((wl_display*)_handle, fd);
        if (client == null)
        {
            throw new WaylandException($"wl_client_create failed for fd {fd}.");
        }

        return GetOrCreateClient((nint)client);
    }

    public IWlGlobal CreateGlobal(WlGlobal owner, WlInterfaceSpec iface, int version) =>
        new LibWaylandGlobal(owner, this, iface, version);

    public void Run() => LibWaylandServer.wl_display_run((wl_display*)_handle);

    public void Terminate() => LibWaylandServer.wl_display_terminate((wl_display*)_handle);

    public void FlushClients() => LibWaylandServer.wl_display_flush_clients((wl_display*)_handle);

    public uint NextSerial() => LibWaylandServer.wl_display_next_serial((wl_display*)_handle);

    private WlServerDisplay.GlobalFilter? _globalFilter;
    private GCHandle _filterSelf;

    public void SetGlobalFilter(WlServerDisplay.GlobalFilter? filter)
    {
        _globalFilter = filter;
        if (filter is null)
        {
            LibWaylandServer.wl_display_set_global_filter((wl_display*)_handle, null, null);
            return;
        }

        if (!_filterSelf.IsAllocated)
        {
            _filterSelf = GCHandle.Alloc(this);
        }

        LibWaylandServer.wl_display_set_global_filter((wl_display*)_handle, &GlobalFilterThunk, (void*)GCHandle.ToIntPtr(_filterSelf));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GlobalFilterThunk(wl_client* client, wl_global* global, void* data)
    {
        var self = (LibWaylandDisplay?)GCHandle.FromIntPtr((nint)data).Target;
        if (self?._globalFilter is not { } filter)
        {
            return 1;
        }

        try
        {
            var iface = LibWaylandServer.wl_global_get_interface(global);
            var name = Marshal.PtrToStringUTF8((nint)iface->name) ?? string.Empty;
            WlGlobal? owned = null;
            var user = LibWaylandServer.wl_global_get_user_data(global);
            if (user != null && GCHandle.FromIntPtr((nint)user).Target is LibWaylandGlobal owner)
            {
                owned = owner.Owner;
            }

            return filter(self.GetOrCreateClient((nint)client), owned, name) ? (byte)1 : (byte)0;
        }
        catch
        {
            // A throwing filter must not unwind into libwayland.
            return 1;
        }
    }

    private Action<WlClient>? _clientCreated;
    private NativeListener* _clientCreatedBlock;

    public Action<WlClient>? ClientCreatedHandler
    {
        get => _clientCreated;
        set
        {
            _clientCreated = value;
            if (value is null || _clientCreatedBlock != null)
            {
                return;
            }

            // libwayland offers no way to unregister, so the block is installed
            // once and freed with the display.
            _clientCreatedBlock = NativeListener.Allocate(&OnClientCreated, this);
            LibWaylandServer.wl_display_add_client_created_listener(
                (wl_display*)_handle, &_clientCreatedBlock->Listener);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClientCreated(wl_listener* listener, void* data)
    {
        if (NativeListener.Target<LibWaylandDisplay>(listener) is not { } display ||
            display._clientCreated is not { } handler)
        {
            return;
        }

        try
        {
            handler(display.GetOrCreateClient((nint)data));
        }
        catch (Exception ex)
        {
            display._owner.CaptureDispatchException(ex);
        }
    }

    public IDisposable AddProtocolLogger(WlProtocolLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        return new LibWaylandProtocolLogger(this, logger);
    }

    public bool SupportsFixes => WlFixesSupport.IsSupported;

    public void AckGlobalRemove(WlClient client, nint fixesHandle, nint registryHandle, uint globalName) =>
        WlFixesSupport.HandleAckGlobalRemove(fixesHandle, registryHandle, globalName);

    public void DestroyRegistry(WlClient client, nint registryHandle) =>
        WlForeignResource.Destroy(registryHandle);

    public IReadOnlyList<WlClient> GetClients()
    {
        var clients = new List<WlClient>();
        var head = LibWaylandServer.wl_display_get_client_list((wl_display*)_handle);

        // The list node is embedded in wl_client, so walk the links rather than
        // asking for a count.
        for (var link = head->next; link != head; link = link->next)
        {
            clients.Add(GetOrCreateClient((nint)LibWaylandServer.wl_client_from_link(link)));
        }

        return clients;
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        LibWaylandServer.wl_display_destroy_clients((wl_display*)_handle);
        LibWaylandServer.wl_display_destroy((wl_display*)_handle);
        _handle = 0;

        if (_clientCreatedBlock != null)
        {
            // The display owned the signal and is gone, so the block is already
            // unlinked.
            NativeListener.Free(_clientCreatedBlock);
            _clientCreatedBlock = null;
        }

        if (_filterSelf.IsAllocated)
        {
            _filterSelf.Free();
        }
    }

    /// <summary>
    /// Interns a wrapper for a native client. Clients connecting through a
    /// listening socket are created inside libwayland, so the wrapper (and its
    /// destroy listener) is established lazily on first contact.
    /// </summary>
    internal WlClient GetOrCreateClient(nint handle) =>
        _clients.GetOrAdd(handle, static (h, self) => self.CreateClientWrapper(h), this);

    private WlClient CreateClientWrapper(nint handle)
    {
        var client = new WlClient(new LibWaylandClient(handle), _owner);

        // One block per client, carrying a handle to this display so the notify
        // callback can find the intern table. Freed in the callback, which runs
        // exactly once for every client.
        var block = NativeListener.Allocate(&OnClientDestroyed, this);
        LibWaylandServer.wl_client_add_destroy_listener((wl_client*)handle, &block->Listener);
        return client;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClientDestroyed(wl_listener* listener, void* data)
    {
        if (NativeListener.Target<LibWaylandDisplay>(listener) is { } display &&
            display._clients.TryRemove((nint)data, out var client))
        {
            client.OnTransportDestroyed();
        }

        NativeListener.Free((NativeListener*)listener);
    }
}

internal sealed unsafe class LibWaylandProtocolLogger : IDisposable
{
    private readonly LibWaylandDisplay _display;
    private readonly WlProtocolLogger _logger;
    private nint _handle;
    private GCHandle _selfHandle;

    internal LibWaylandProtocolLogger(LibWaylandDisplay display, WlProtocolLogger logger)
    {
        _display = display;
        _logger = logger;
        _selfHandle = GCHandle.Alloc(this);
        var handle = LibWaylandServer.wl_display_add_protocol_logger(
            (wl_display*)display.RawHandle, &LogThunk, (void*)GCHandle.ToIntPtr(_selfHandle));
        if (handle == null)
        {
            _selfHandle.Free();
            throw new WaylandException("wl_display_add_protocol_logger failed.");
        }

        _handle = (nint)handle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LogThunk(void* data, wl_protocol_logger_type type, wl_protocol_logger_message* message)
    {
        if (GCHandle.FromIntPtr((nint)data).Target is not LibWaylandProtocolLogger self)
        {
            return;
        }

        try
        {
            var direction = type == wl_protocol_logger_type.WL_PROTOCOL_LOGGER_REQUEST
                ? WlProtocolMessageDirection.Request
                : WlProtocolMessageDirection.Event;
            var logged = new WlProtocolMessage(direction, message);
            self._logger(in logged);
        }
        catch (Exception ex)
        {
            // A throwing logger must not unwind into libwayland.
            self._display.Owner.CaptureDispatchException(ex);
        }
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        LibWaylandServer.wl_protocol_logger_destroy((wl_protocol_logger*)_handle);
        _handle = 0;
        _selfHandle.Free();
    }
}

internal sealed unsafe class LibWaylandEventLoop : IWlEventLoop
{
    internal LibWaylandEventLoop(nint handle)
    {
        RawHandle = handle;
    }

    public nint RawHandle { get; }

    public int Dispatch(int timeoutMs) =>
        LibWaylandServer.wl_event_loop_dispatch((wl_event_loop*)RawHandle, timeoutMs);

    public IWlEventSource AddFd(int fd, WlFdEvents events, Action<int, WlFdEvents> callback) =>
        LibWaylandEventSource.AddFd(this, fd, events, callback);

    public IWlEventSource AddTimer(Action callback) =>
        LibWaylandEventSource.AddTimer(this, callback);

    public IWlEventSource AddIdle(Action callback) =>
        LibWaylandEventSource.AddIdle(this, callback);

    public IWlEventSource AddSignal(int signalNumber, Action<int> callback) =>
        LibWaylandEventSource.AddSignal(this, signalNumber, callback);

    public int Fd => LibWaylandServer.wl_event_loop_get_fd((wl_event_loop*)RawHandle);

    public void DispatchIdle() =>
        LibWaylandServer.wl_event_loop_dispatch_idle((wl_event_loop*)RawHandle);
}

internal sealed unsafe class LibWaylandEventSource : IWlEventSource
{
    private readonly Action<int, WlFdEvents>? _fdCallback;
    private readonly Action<int>? _signalCallback;
    private readonly Action? _callback;
    private readonly bool _oneShot;
    private nint _handle;
    private GCHandle _selfHandle;

    private LibWaylandEventSource(
        Action<int, WlFdEvents>? fdCallback,
        Action? callback,
        bool oneShot,
        Action<int>? signalCallback = null)
    {
        _fdCallback = fdCallback;
        _signalCallback = signalCallback;
        _callback = callback;
        _oneShot = oneShot;
        _selfHandle = GCHandle.Alloc(this);
    }

    public bool IsRemoved => _handle == 0;

    internal static LibWaylandEventSource AddFd(LibWaylandEventLoop loop, int fd, WlFdEvents events, Action<int, WlFdEvents> callback)
    {
        var source = new LibWaylandEventSource(callback, null, oneShot: false);
        var handle = LibWaylandServer.wl_event_loop_add_fd(
            (wl_event_loop*)loop.RawHandle, fd, (uint)events, &FdThunk, (void*)GCHandle.ToIntPtr(source._selfHandle));
        return source.Register((nint)handle, $"wl_event_loop_add_fd({fd})");
    }

    internal static LibWaylandEventSource AddTimer(LibWaylandEventLoop loop, Action callback)
    {
        var source = new LibWaylandEventSource(null, callback, oneShot: false);
        var handle = LibWaylandServer.wl_event_loop_add_timer(
            (wl_event_loop*)loop.RawHandle, &TimerThunk, (void*)GCHandle.ToIntPtr(source._selfHandle));
        return source.Register((nint)handle, "wl_event_loop_add_timer");
    }

    internal static LibWaylandEventSource AddSignal(LibWaylandEventLoop loop, int signalNumber, Action<int> callback)
    {
        var source = new LibWaylandEventSource(null, null, oneShot: false, signalCallback: callback);
        var handle = LibWaylandServer.wl_event_loop_add_signal(
            (wl_event_loop*)loop.RawHandle, signalNumber, &SignalThunk, (void*)GCHandle.ToIntPtr(source._selfHandle));
        return source.Register((nint)handle, $"wl_event_loop_add_signal({signalNumber})");
    }

    internal static LibWaylandEventSource AddIdle(LibWaylandEventLoop loop, Action callback)
    {
        var source = new LibWaylandEventSource(null, callback, oneShot: true);
        var handle = LibWaylandServer.wl_event_loop_add_idle(
            (wl_event_loop*)loop.RawHandle, &IdleThunk, (void*)GCHandle.ToIntPtr(source._selfHandle));
        return source.Register((nint)handle, "wl_event_loop_add_idle");
    }

    private LibWaylandEventSource Register(nint handle, string what)
    {
        if (handle == 0)
        {
            _selfHandle.Free();
            throw new WaylandException($"{what} failed.");
        }

        _handle = handle;
        return this;
    }

    public void Remove()
    {
        if (_handle == 0)
        {
            return;
        }

        LibWaylandServer.wl_event_source_remove((wl_event_source*)_handle);
        Release();
    }

    public void UpdateTimer(int delayMs)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (LibWaylandServer.wl_event_source_timer_update((wl_event_source*)_handle, delayMs) != 0)
        {
            throw new WaylandException("wl_event_source_timer_update failed.");
        }
    }

    public void UpdateFd(WlFdEvents events)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (LibWaylandServer.wl_event_source_fd_update((wl_event_source*)_handle, (uint)events) != 0)
        {
            throw new WaylandException("wl_event_source_fd_update failed.");
        }
    }

    private void Release()
    {
        _handle = 0;
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FdThunk(int fd, uint mask, void* data)
    {
        if (GCHandle.FromIntPtr((nint)data).Target is LibWaylandEventSource source)
        {
            source._fdCallback!(fd, (WlFdEvents)mask);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SignalThunk(int signalNumber, void* data)
    {
        if (GCHandle.FromIntPtr((nint)data).Target is LibWaylandEventSource source)
        {
            source._signalCallback!(signalNumber);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TimerThunk(void* data)
    {
        if (GCHandle.FromIntPtr((nint)data).Target is LibWaylandEventSource source)
        {
            source._callback!();
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void IdleThunk(void* data)
    {
        if (GCHandle.FromIntPtr((nint)data).Target is LibWaylandEventSource source)
        {
            // libwayland frees an idle source after it fires; release our side
            // first so a callback exception cannot leave a dangling handle.
            source.Release();
            source._callback!();
        }
    }
}

internal sealed unsafe class LibWaylandClient : IWlClient
{
    internal LibWaylandClient(nint handle)
    {
        RawHandle = handle;
    }

    public nint RawHandle { get; }

    public void Flush() => LibWaylandServer.wl_client_flush((wl_client*)RawHandle);

    public void Destroy() => LibWaylandServer.wl_client_destroy((wl_client*)RawHandle);

    internal readonly Stack<LibWaylandResource> ResourcePool = new(LibWaylandResource.PoolLimit);

    public IWlResource CreateResource(WlResource owner, WlInterfaceSpec spec, uint version, uint id)
    {
        if (ResourcePool.TryPop(out var recycled))
        {
            recycled.Reinitialize(owner, this, spec, version, id);
            return recycled;
        }

        return new LibWaylandResource(owner, this, spec, version, id);
    }

    public WlClientCredentials GetCredentials()
    {
        // Read every time: a cached pid outlives correctness for a long-lived wrapper.
        int pid;
        uint uid, gid;
        LibWaylandServer.wl_client_get_credentials((wl_client*)RawHandle, &pid, &uid, &gid);
        return new WlClientCredentials(pid, uid, gid);
    }

    public int Fd => LibWaylandServer.wl_client_get_fd((wl_client*)RawHandle);

    public nint GetObjectHandle(uint id) =>
        (nint)LibWaylandServer.wl_client_get_object((wl_client*)RawHandle, id);

    public void CloseFd(int fd)
    {
        if (fd >= 0)
        {
            close(fd);
        }
    }

    [DllImport("libc", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int close(int fd);
}

internal sealed unsafe class LibWaylandResource : IWlResource
{
    internal const int PoolLimit = 64;

    private WlResource _owner;
    private WlInterfaceSpec _spec;
    private LibWaylandClient _client;
    private nint _handle;
    private GCHandle _selfHandle;

    internal LibWaylandResource(WlResource owner, LibWaylandClient client, WlInterfaceSpec spec, uint version, uint id)
    {
        _owner = owner;
        _spec = spec;
        _client = client;
        _selfHandle = GCHandle.Alloc(this);
        Attach(client, spec, version, id);
    }

    internal void Reinitialize(WlResource owner, LibWaylandClient client, WlInterfaceSpec spec, uint version, uint id)
    {
        _owner = owner;
        _spec = spec;
        _client = client;
        Attach(client, spec, version, id);
    }

    private void Attach(LibWaylandClient client, WlInterfaceSpec spec, uint version, uint id)
    {
        var resource = LibWaylandServer.wl_resource_create(
            (wl_client*)client.RawHandle, spec.NativePointer, (int)version, id);
        if (resource == null)
        {
            throw new WaylandException($"wl_resource_create failed for '{spec.Name}' v{version} id {id}.");
        }

        _handle = (nint)resource;
        var self = (void*)GCHandle.ToIntPtr(_selfHandle);
        LibWaylandServer.wl_resource_set_dispatcher(resource, &DispatchThunk, self, self, &DestroyThunk);
    }

    public nint RawHandle => _handle;

    public uint Id => LibWaylandServer.wl_resource_get_id((wl_resource*)_handle);

    public uint Version => (uint)LibWaylandServer.wl_resource_get_version((wl_resource*)_handle);

    public void PostEvent(uint opcode, ReadOnlySpan<WlArg> args)
    {
        fixed (WlArg* argsPtr = args)
        {
            LibWaylandServer.wl_resource_post_event_array(
                (wl_resource*)_handle, opcode, (wl_argument*)argsPtr);
        }
    }

    public void PostError(uint code, string message)
    {
        var format = Marshal.StringToCoTaskMemUTF8("%s");
        var text = Marshal.StringToCoTaskMemUTF8(message);
        try
        {
            LibWaylandServer.wl_resource_post_error_fixed(
                (wl_resource*)_handle, code, (sbyte*)format, (sbyte*)text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
            Marshal.FreeCoTaskMem(text);
        }
    }

    public void Destroy() => LibWaylandServer.wl_resource_destroy((wl_resource*)_handle);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DispatchThunk(void* implementation, void* target, uint opcode, wl_message* message, wl_argument* args)
    {
        var impl = (LibWaylandResource?)GCHandle.FromIntPtr((nint)implementation).Target;
        if (impl is null || impl._handle == 0)
        {
            return 0;
        }

        try
        {
            var spec = impl._spec;
            var argCount = opcode < spec.Requests.Count ? spec.Requests[(int)opcode].WireArgCount : 0;
            impl._owner.DispatchIncoming(opcode, new ReadOnlySpan<WlArg>(args, argCount));
        }
        catch (Exception ex)
        {
            impl._owner.Client.Display?.CaptureDispatchException(ex);
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
        if (handle.Target is LibWaylandResource impl)
        {
            impl.HandleDestroyed();
        }
    }

    private void HandleDestroyed()
    {
        var handle = _handle;
        var owner = _owner;
        var client = _client;
        _handle = 0;
        owner.OnTransportDestroyed(handle);

        if (_handle != 0)
        {
            return;
        }

        if (client.ResourcePool.Count < PoolLimit)
        {
            client.ResourcePool.Push(this);
        }
        else if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }
}

internal sealed unsafe class LibWaylandGlobal : IWlGlobal
{
    private readonly WlGlobal _owner;

    internal WlGlobal Owner => _owner;
    private readonly LibWaylandDisplay _display;
    private nint _handle;
    private GCHandle _selfHandle;

    internal LibWaylandGlobal(WlGlobal owner, LibWaylandDisplay display, WlInterfaceSpec iface, int version)
    {
        _owner = owner;
        _display = display;
        _selfHandle = GCHandle.Alloc(this);
        var global = LibWaylandServer.wl_global_create(
            (wl_display*)display.RawHandle,
            iface.NativePointer,
            version,
            (void*)GCHandle.ToIntPtr(_selfHandle),
            &BindThunk);
        if (global == null)
        {
            _selfHandle.Free();
            throw new WaylandException($"wl_global_create failed for '{iface.Name}' v{version}.");
        }

        _handle = (nint)global;
    }

    /// <summary>
    /// wl_global_get_name arrived in libwayland 1.22, which is recent enough that
    /// a supported distribution may not have it.
    /// </summary>
    private static readonly bool HasGetName = NativeFeatures.ServerHas("wl_global_get_name");

    public uint NameFor(WlClient client)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (!HasGetName)
        {
            throw new WaylandException(
                "wl_global_get_name requires libwayland 1.22 or newer; the loaded libwayland-server.so.0 does not export it.");
        }

        return LibWaylandServer.wl_global_get_name((wl_global*)_handle, (wl_client*)client.RawHandle);
    }

    public uint Version
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return LibWaylandServer.wl_global_get_version((wl_global*)_handle);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BindThunk(wl_client* client, void* data, uint version, uint id)
    {
        var impl = (LibWaylandGlobal?)GCHandle.FromIntPtr((nint)data).Target;
        if (impl is null)
        {
            return;
        }

        try
        {
            impl._owner.HandleBind(impl._display.GetOrCreateClient((nint)client), version, id);
        }
        catch (Exception ex)
        {
            impl._display.Owner.CaptureDispatchException(ex);
        }
    }

    /// <summary>
    /// wl_global_set_withdrawn_listener is libwayland 1.26; without it a caller
    /// has to fall back to a grace period.
    /// </summary>
    internal static readonly bool HasWithdrawnListener =
        NativeFeatures.ServerHas("wl_global_set_withdrawn_listener");

    private bool _removed;
    private Action? _withdrawn;

    public bool SupportsWithdrawn => HasWithdrawnListener;

    public Action? WithdrawnHandler
    {
        get => _withdrawn;
        set
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            if (value is not null && !HasWithdrawnListener)
            {
                throw new WaylandException(
                    "wl_global_set_withdrawn_listener requires libwayland 1.26 or newer; the loaded libwayland-server.so.0 does not export it.");
            }

            var first = _withdrawn is null;
            _withdrawn = value;
            if (value is not null && first)
            {
                // The callback takes no user data, so the managed object is
                // recovered from the global's own user data, as BindThunk does.
                LibWaylandServer.wl_global_set_withdrawn_listener((wl_global*)_handle, &WithdrawnThunk);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WithdrawnThunk(wl_global* global)
    {
        var data = LibWaylandServer.wl_global_get_user_data(global);
        if (data == null || GCHandle.FromIntPtr((nint)data).Target is not LibWaylandGlobal impl)
        {
            return;
        }

        try
        {
            impl._withdrawn?.Invoke();
        }
        catch (Exception ex)
        {
            impl._display.Owner.CaptureDispatchException(ex);
        }
    }

    public void Remove()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);

        // wl_global_remove aborts the process when called twice, so the second
        // call must never reach libwayland.
        if (_removed)
        {
            throw new InvalidOperationException(
                $"The '{_owner.InterfaceName}' global has already been removed.");
        }

        _removed = true;
        LibWaylandServer.wl_global_remove((wl_global*)_handle);
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        LibWaylandServer.wl_global_destroy((wl_global*)_handle);
        _handle = 0;
        _selfHandle.Free();
    }
}
