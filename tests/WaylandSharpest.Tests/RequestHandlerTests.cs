using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

public class RequestHandlerTests : LoopbackHarness
{
    public RequestHandlerTests()
    {
    }

    protected RequestHandlerTests(IWlServerTransport transport)
        : base(transport)
    {
    }

    private sealed class SurfaceHandler : IWlSurfaceHandler
    {
        public List<string> Calls { get; } = [];

        public void Destroy(WlSurfaceResource resource) => Calls.Add("destroy");

        public void Attach(WlSurfaceResource resource, in WlSurfaceResource.AttachEventArgs args) => Calls.Add($"attach {args.X},{args.Y}");

        public void Damage(WlSurfaceResource resource, in WlSurfaceResource.DamageEventArgs args) => Calls.Add("damage");

        public void Frame(WlSurfaceResource resource, in WlSurfaceResource.FrameEventArgs args) => Calls.Add("frame");

        public void SetOpaqueRegion(WlSurfaceResource resource, in WlSurfaceResource.SetOpaqueRegionEventArgs args) => Calls.Add("opaque");

        public void SetInputRegion(WlSurfaceResource resource, in WlSurfaceResource.SetInputRegionEventArgs args) => Calls.Add("input");

        public void Commit(WlSurfaceResource resource) => Calls.Add("commit");

        public void SetBufferTransform(WlSurfaceResource resource, in WlSurfaceResource.SetBufferTransformEventArgs args) => Calls.Add("transform");

        public void SetBufferScale(WlSurfaceResource resource, in WlSurfaceResource.SetBufferScaleEventArgs args) => Calls.Add("scale");

        public void DamageBuffer(WlSurfaceResource resource, in WlSurfaceResource.DamageBufferEventArgs args) => Calls.Add("damage_buffer");

        public void Offset(WlSurfaceResource resource, in WlSurfaceResource.OffsetEventArgs args) => Calls.Add("offset");

        public void GetRelease(WlSurfaceResource resource, in WlSurfaceResource.GetReleaseEventArgs args) => Calls.Add("get_release");
    }

    private WlSurfaceResource? _surface;

    private IDisposable ServeSurfaces(IWlSurfaceHandler handler) =>
        Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) => _surface = new WlSurfaceResource(client, version, e.Id, handler);
        });

    [Fact]
    public void A_handler_receives_each_request_and_the_events_stay_silent()
    {
        var handler = new SurfaceHandler();
        using var global = ServeSurfaces(handler);
        using var compositor = Bind<WlCompositor>("wl_compositor", 6);
        var surface = compositor.CreateSurface();
        PumpToServer();
        var events = 0;
        _surface!.Commit += (_, _) => events++;

        surface.Attach(null, 3, 4);
        surface.Commit();
        PumpToServer();

        Assert.Equal(["attach 3,4", "commit"], handler.Calls);
        Assert.Equal(0, events);
        surface.Dispose();
        PumpToServer();
    }

    [Fact]
    public void A_destructor_reaches_the_handler_and_destroys_the_resource()
    {
        var handler = new SurfaceHandler();
        using var global = ServeSurfaces(handler);
        using var compositor = Bind<WlCompositor>("wl_compositor", 6);
        var surface = compositor.CreateSurface();
        PumpToServer();

        surface.Dispose();
        PumpToServer();

        Assert.Equal(["destroy"], handler.Calls);
        Assert.True(_surface!.IsDestroyed);
    }

    [Fact]
    public void A_destroyed_resource_lets_go_of_its_handler_and_its_destroy_subscribers()
    {
        var handler = new WeakReference(null);
        var subscriber = new WeakReference(null);
        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) => _surface = Create(client, version, e.Id, handler, subscriber);
        });
        using var compositor = Bind<WlCompositor>("wl_compositor", 6);
        var surface = compositor.CreateSurface();
        PumpToServer();
        surface.Dispose();
        PumpToServer();
        Assert.True(_surface!.IsDestroyed);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(handler.IsAlive, "a destroyed resource still holds its handler");
        Assert.False(subscriber.IsAlive, "a destroyed resource still holds its Destroyed subscribers");
        GC.KeepAlive(_surface);
    }

    private static WlSurfaceResource Create(WlClient client, uint version, uint id, WeakReference handler, WeakReference subscriber)
    {
        var surfaceHandler = new SurfaceHandler();
        handler.Target = surfaceHandler;
        var resource = new WlSurfaceResource(client, version, id, surfaceHandler);
        var owner = new object();
        subscriber.Target = owner;
        resource.Destroyed += (_, _) => GC.KeepAlive(owner);
        return resource;
    }
}

public sealed class RequestHandlerTestsManaged() : RequestHandlerTests(new global::Wayland.Server.ManagedTransport());
