using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>
/// Implementation of <see cref="IWlServerTransport"/> using <c>libwayland-server</c>.
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

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        LibWaylandServer.wl_display_destroy_clients((wl_display*)_handle);
        LibWaylandServer.wl_display_destroy((wl_display*)_handle);
        _handle = 0;
    }

    internal WlClient GetOrCreateClient(nint handle) =>
        _clients.GetOrAdd(handle, static (h, self) => self.CreateClientWrapper(h), this);

    private WlClient CreateClientWrapper(nint handle)
    {
        var client = new WlClient(new LibWaylandClient(handle), _owner);

        var block = (ClientDestroyListener*)Marshal.AllocHGlobal(sizeof(ClientDestroyListener));
        block->Listener = default;
        block->Listener.notify = &OnClientDestroyed;
        block->Display = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        LibWaylandServer.wl_client_add_destroy_listener((wl_client*)handle, &block->Listener);
        return client;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientDestroyListener
    {
        public wl_listener Listener;
        public nint Display;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClientDestroyed(wl_listener* listener, void* data)
    {
        var block = (ClientDestroyListener*)listener;
        var displayHandle = GCHandle.FromIntPtr(block->Display);
        if (displayHandle.Target is LibWaylandDisplay display &&
            display._clients.TryRemove((nint)data, out var client))
        {
            client.OnTransportDestroyed();
        }

        displayHandle.Free();
        Marshal.FreeHGlobal((nint)block);
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

    public IWlResource CreateResource(WlResource owner, WlInterfaceSpec spec, uint version, uint id) =>
        new LibWaylandResource(owner, this, spec, version, id);
}

internal sealed unsafe class LibWaylandResource : IWlResource
{
    private readonly WlResource _owner;
    private readonly WlInterfaceSpec _spec;
    private nint _handle;
    private GCHandle _selfHandle;

    internal LibWaylandResource(WlResource owner, LibWaylandClient client, WlInterfaceSpec spec, uint version, uint id)
    {
        _owner = owner;
        _spec = spec;
        var resource = LibWaylandServer.wl_resource_create(
            (wl_client*)client.RawHandle, spec.NativePointer, (int)version, id);
        if (resource == null)
        {
            throw new WaylandException($"wl_resource_create failed for '{spec.Name}' v{version} id {id}.");
        }

        _handle = (nint)resource;
        _selfHandle = GCHandle.Alloc(this);
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
        _handle = 0;
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _owner.OnTransportDestroyed(handle);
    }
}

internal sealed unsafe class LibWaylandGlobal : IWlGlobal
{
    private readonly WlGlobal _owner;
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
