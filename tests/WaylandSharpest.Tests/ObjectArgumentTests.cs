using System.Runtime.InteropServices;
using Wayland;
using Wayland.Native;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Tests for object-argument decoding: owned objects resolve through the
/// pointer-keyed registry, objects created by other libraries on the same
/// display resolve to null (their user data is never dereferenced), and the
/// raw-handle accessors expose the native pointer for bridging.
/// </summary>
public sealed class ObjectArgumentTests : IDisposable
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int socketpair(int domain, int type, int protocol, int* sv);

    private readonly WlServerDisplay _server;
    private readonly WlDisplay _client;

    public ObjectArgumentTests()
    {
        _server = WlServerDisplay.Create();
        int fd0, fd1;
        unsafe
        {
            var fds = stackalloc int[2];
            Assert.Equal(0, socketpair(AF_UNIX, SOCK_STREAM, 0, fds));
            fd0 = fds[0];
            fd1 = fds[1];
        }

        _server.CreateClient(fd0);
        _client = WlDisplay.ConnectToFd(fd1);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    private void PumpToClient()
    {
        _client.Flush();
        _server.EventLoop.Dispatch(100);
        _server.FlushClients();
        _client.Dispatch();
    }

    private void PumpToServer()
    {
        _client.Flush();
        _server.EventLoop.Dispatch(100);
    }

    private WlCompositor BindCompositor(WlGlobal global)
    {
        using var registry = _client.GetRegistry();
        uint name = 0;
        registry.Global += (_, e) =>
        {
            if (e.Interface == "wl_compositor")
            {
                name = e.Name;
            }
        };
        PumpToClient();
        Assert.NotEqual(0u, name);
        return registry.Bind<WlCompositor>(name, 6);
    }

    /// <summary>Exposes the protected static decode helpers; never instantiated.</summary>
    private sealed class ResourceProbe : WlResource
    {
        private ResourceProbe() : base(null!, null!, 0, 0)
        {
        }

        protected override WlInterfaceSpec Spec => throw new NotSupportedException();

        protected override void HandleRequest(uint opcode, ReadOnlySpan<WlArg> args) => throw new NotSupportedException();

        public static T? Decode<T>(nint handle) where T : WlResource => GetResource<T>(new WlArg { Ptr = handle });

        public static nint DecodeHandle(nint handle) => GetResourceHandle(new WlArg { Ptr = handle });
    }

    /// <summary>Exposes the protected static decode helpers; never instantiated.</summary>
    private sealed class ProxyProbe : WlProxy
    {
        private ProxyProbe() : base(0, null)
        {
        }

        protected override WlInterfaceSpec Spec => throw new NotSupportedException();

        protected override void HandleEvent(uint opcode, ReadOnlySpan<WlArg> args) => throw new NotSupportedException();

        public static T? Decode<T>(nint handle) where T : WlProxy => GetProxy<T>(new WlArg { Ptr = handle });

        public static nint DecodeHandle(nint handle) => GetProxyHandle(new WlArg { Ptr = handle });
    }

    [Fact]
    public void Object_arguments_resolve_to_owned_resources()
    {
        WlRegionResource? serverRegion = null;
        WlSurfaceResource? serverSurface = null;
        WlRegionResource? decodedRegion = null;
        nint decodedHandle = 0;

        using var global = _server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) =>
            {
                serverSurface = new WlSurfaceResource(client, version, e.Id);
                serverSurface.SetInputRegion += (_, e) =>
                {
                    decodedRegion = e.Region;
                    decodedHandle = e.RegionHandle;
                };
            };
            compositor.CreateRegion += (_, e) => serverRegion = new WlRegionResource(client, version, e.Id);
        });

        using var compositor = BindCompositor(global);
        using var surface = compositor.CreateSurface();
        using var region = compositor.CreateRegion();
        surface.SetInputRegion(region);
        PumpToServer();

        Assert.NotNull(serverRegion);
        Assert.Same(serverRegion, decodedRegion);
        Assert.Equal(serverRegion!.RawHandle, decodedHandle);
    }

    [Fact]
    public void Foreign_resource_argument_decodes_to_null_with_usable_handle()
    {
        // Simulates a resource owned by another native library (e.g. wlroots):
        // created behind WaylandSharpest's back, with user_data pointing at a
        // plain heap allocation rather than a GCHandle.
        var foreignUserData = Marshal.AllocHGlobal(16);
        nint foreignRegion = 0;
        var setInputRegionRaised = false;
        WlRegionResource? decodedRegion = null;
        nint decodedHandle = 0;

        try
        {
            using var global = _server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
            {
                var compositor = new WlCompositorResource(client, version, id);
                compositor.CreateSurface += (_, e) =>
                {
                    var serverSurface = new WlSurfaceResource(client, version, e.Id);
                    serverSurface.SetInputRegion += (_, e) =>
                    {
                        setInputRegionRaised = true;
                        decodedRegion = e.Region;
                        decodedHandle = e.RegionHandle;
                    };
                };
                compositor.CreateRegion += (_, e) =>
                {
                    unsafe
                    {
                        var resource = LibWaylandServer.wl_resource_create(
                            (wl_client*)client.RawHandle,
                            (wl_interface*)WlRegion.Interface.NativeHandle,
                            (int)version,
                            e.Id);
                        LibWaylandServer.wl_resource_set_user_data(resource, (void*)foreignUserData);
                        foreignRegion = (nint)resource;
                    }
                };
            });

            using var compositor = BindCompositor(global);
            using var surface = compositor.CreateSurface();
            // Not disposed: the foreign resource has no request handlers, so the
            // destructor request must never reach it.
            var region = compositor.CreateRegion();
            surface.SetInputRegion(region);
            PumpToServer();

            Assert.True(setInputRegionRaised);
            Assert.NotEqual(0, foreignRegion);
            Assert.Null(decodedRegion);
            Assert.Equal(foreignRegion, decodedHandle);
        }
        finally
        {
            Marshal.FreeHGlobal(foreignUserData);
        }
    }

    [Fact]
    public void Destroyed_resources_are_deregistered()
    {
        WlSurfaceResource? serverSurface = null;
        using var global = _server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) => serverSurface = new WlSurfaceResource(client, version, e.Id);
        });

        using var compositor = BindCompositor(global);
        var surface = compositor.CreateSurface();
        PumpToServer();

        Assert.NotNull(serverSurface);
        var handle = serverSurface!.RawHandle;
        Assert.Same(serverSurface, ResourceProbe.Decode<WlSurfaceResource>(handle));
        Assert.Equal(handle, ResourceProbe.DecodeHandle(handle));

        surface.Dispose();
        PumpToServer();

        Assert.True(serverSurface.IsDestroyed);
        Assert.Null(ResourceProbe.Decode<WlSurfaceResource>(handle));
    }

    [Fact]
    public void Object_arguments_resolve_to_owned_proxies()
    {
        WlSurfaceResource? serverSurface = null;
        WlPointerResource? serverPointer = null;

        using var compositorGlobal = _server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) => serverSurface = new WlSurfaceResource(client, version, e.Id);
        });
        using var seatGlobal = _server.CreateGlobal(WlSeat.Interface, 5, (client, version, id) =>
        {
            var seat = new WlSeatResource(client, version, id);
            seat.GetPointer += (_, e) => serverPointer = new WlPointerResource(client, version, e.Id);
        });

        using var registry = _client.GetRegistry();
        uint compositorName = 0, seatName = 0;
        registry.Global += (_, e) =>
        {
            if (e.Interface == "wl_compositor")
            {
                compositorName = e.Name;
            }
            else if (e.Interface == "wl_seat")
            {
                seatName = e.Name;
            }
        };
        PumpToClient();

        using var compositor = registry.Bind<WlCompositor>(compositorName, 6);
        using var seat = registry.Bind<WlSeat>(seatName, 5);
        using var surface = compositor.CreateSurface();
        using var pointer = seat.GetPointer();

        WlSurface? decodedSurface = null;
        nint decodedHandle = 0;
        pointer.Enter += (_, e) =>
        {
            decodedSurface = e.Surface;
            decodedHandle = e.SurfaceHandle;
        };

        PumpToServer();
        Assert.NotNull(serverSurface);
        Assert.NotNull(serverPointer);

        serverPointer!.SendEnter(1, serverSurface!, WlFixed.FromInt(10), WlFixed.FromInt(20));
        _server.FlushClients();
        _client.Dispatch();

        Assert.Same(surface, decodedSurface);
        Assert.Equal(surface.RawHandle, decodedHandle);
    }

    [Fact]
    public void Unknown_proxy_pointer_decodes_to_null()
    {
        // A pointer that was never a WaylandSharpest proxy must resolve to null
        // without being dereferenced.
        var bogus = Marshal.AllocHGlobal(16);
        try
        {
            Assert.Null(ProxyProbe.Decode<WlSurface>(bogus));
            Assert.Equal(bogus, ProxyProbe.DecodeHandle(bogus));
        }
        finally
        {
            Marshal.FreeHGlobal(bogus);
        }
    }

    [Fact]
    public void Disposed_proxies_are_deregistered()
    {
        Assert.Same(_client, ProxyProbe.Decode<WlDisplay>(_client.RawHandle));

        var registry = _client.GetRegistry();
        var handle = registry.RawHandle;
        Assert.Same(registry, ProxyProbe.Decode<WlRegistry>(handle));

        registry.Dispose();
        Assert.Null(ProxyProbe.Decode<WlRegistry>(handle));
    }
}
