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
public class ObjectArgumentTests : LoopbackHarness
{
    /// <summary>Runs against libwayland.</summary>
    public ObjectArgumentTests()
    {
    }

    /// <summary>Runs against the transport a twin supplies.</summary>
    protected ObjectArgumentTests(global::Wayland.Server.IWlServerTransport transport) : base(transport)
    {
    }

    private WlCompositor BindCompositor(WlGlobal global) => Bind<WlCompositor>("wl_compositor", 6);

    /// <summary>Exposes the protected static decode helpers; never instantiated.</summary>
    private sealed class ResourceProbe : WlResource
    {
        private ResourceProbe() : base(null!, null!, 0, 0)
        {
        }

        protected override WlInterfaceSpec Spec => throw new NotSupportedException();

        protected override void HandleRequest(uint opcode, ReadOnlySpan<WlArg> args) => throw new NotSupportedException();

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

        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
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
    public void Destroyed_resources_are_deregistered()
    {
        WlSurfaceResource? serverSurface = null;
        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) => serverSurface = new WlSurfaceResource(client, version, e.Id);
        });

        using var compositor = BindCompositor(global);
        var surface = compositor.CreateSurface();
        PumpToServer();

        Assert.NotNull(serverSurface);
        var handle = serverSurface!.RawHandle;
        var id = serverSurface.Id;
        Assert.Same(serverSurface, ServerClient.GetObject(id));
        Assert.Equal(handle, ResourceProbe.DecodeHandle(handle));

        surface.Dispose();
        PumpToServer();

        Assert.True(serverSurface.IsDestroyed);
        Assert.Null(ServerClient.GetObject(id));
    }

    [Fact]
    public void Object_arguments_resolve_to_owned_proxies()
    {
        WlSurfaceResource? serverSurface = null;
        WlPointerResource? serverPointer = null;

        using var compositorGlobal = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) => serverSurface = new WlSurfaceResource(client, version, e.Id);
        });
        using var seatGlobal = Server.CreateGlobal(WlSeat.Interface, 5, (client, version, id) =>
        {
            var seat = new WlSeatResource(client, version, id);
            seat.GetPointer += (_, e) => serverPointer = new WlPointerResource(client, version, e.Id);
        });

        using var registry = Client.GetRegistry();
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
        Server.FlushClients();
        Client.Dispatch();

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
        Assert.Same(Client, ProxyProbe.Decode<WlDisplay>(Client.RawHandle));

        var registry = Client.GetRegistry();
        var handle = registry.RawHandle;
        Assert.Same(registry, ProxyProbe.Decode<WlRegistry>(handle));

        registry.Dispose();
        Assert.Null(ProxyProbe.Decode<WlRegistry>(handle));
    }
}

/// <summary>The same tests, against the managed transport.</summary>
[Trait("Transport", "Managed")]
public sealed class ObjectArgumentTestsManaged() : ObjectArgumentTests(new global::Wayland.Server.ManagedTransport());
