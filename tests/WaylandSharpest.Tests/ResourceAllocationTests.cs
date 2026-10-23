using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

[LibWaylandOnly("It asserts that libwayland transport objects are recycled, which is a property of that transport and not of the protocol.")]
public sealed class ResourceAllocationTests : LoopbackHarness
{
    [Fact]
    public void A_throwing_client_destroyed_handler_surfaces_from_dispatch()
    {
        ServerClient.Destroyed += () => throw new InvalidOperationException("handler threw");

        ServerClient.Destroy();

        var thrown = Assert.Throws<WaylandException>(() => Server.EventLoop.Dispatch(0));
        Assert.Equal("handler threw", thrown.InnerException?.Message);
    }

    private const int Rounds = 200;

    private const long BytesPerHundredCallbacks = 4800;

    [Fact]
    public void Creating_and_destroying_a_callback_stays_within_budget()
    {
        var client = ServerClient;

        for (var i = 0; i < 3; i++)
        {
            Churn(client, Rounds);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Churn(client, Rounds);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(BytesPerHundredCallbacks, allocated * 100 / Rounds);
    }

    [Fact]
    public void A_destroyed_client_keeps_no_pooled_transport_objects()
    {
        var client = ServerClient;
        var transport = Assert.IsType<LibWaylandClient>(client.Impl);
        var callbacks = new WlCallbackResource[8];
        for (var i = 0; i < callbacks.Length; i++)
        {
            callbacks[i] = new WlCallbackResource(client, 1, 0);
        }

        foreach (var callback in callbacks)
        {
            callback.Destroy();
        }

        Assert.Equal(8, transport.ResourcePool.Count);
        for (var i = 0; i < 4; i++)
        {
            _ = new WlCallbackResource(client, 1, 0);
        }

        client.Destroy();

        Assert.True(transport.Closed);
        Assert.Equal(0, transport.ResourcePool.Count);
    }

    [Fact]
    public void A_recycled_transport_object_does_not_alias_the_resource_it_served()
    {
        var first = new WlCallbackResource(ServerClient, 1, 0);
        var firstHandle = first.RawHandle;
        var firstId = first.Id;
        first.Destroy();

        Assert.True(first.IsDestroyed);
        Assert.Null(ServerClient.GetObject(firstId));

        var second = new WlCallbackResource(ServerClient, 1, 0);

        Assert.False(second.IsDestroyed);
        Assert.NotEqual(0, second.RawHandle);
        Assert.True(first.IsDestroyed);
        Assert.Same(second, ServerClient.GetObject(second.Id));
        Assert.NotSame(first, ServerClient.GetObject(second.Id));

        second.Destroy();
        Assert.NotEqual(firstHandle, 0);
    }

    [Fact]
    public void A_resource_created_while_another_is_being_destroyed_gets_its_own_transport()
    {
        WlCallbackResource? replacement = null;
        var victim = new WlCallbackResource(ServerClient, 1, 0);
        victim.Destroyed += (_, _) => replacement = new WlCallbackResource(ServerClient, 1, 0);

        victim.Destroy();

        Assert.NotNull(replacement);
        Assert.True(victim.IsDestroyed);
        Assert.False(replacement!.IsDestroyed);
        Assert.NotEqual(0, replacement.RawHandle);
        Assert.Same(replacement, ServerClient.GetObject(replacement.Id));

        replacement.Destroy();
    }

    [Fact]
    public void A_recycled_transport_object_still_delivers_requests_to_its_new_owner()
    {
        WlSurfaceResource? firstSurface = null;
        WlSurfaceResource? secondSurface = null;
        var firstCommits = 0;
        var secondCommits = 0;

        using var global = Server.CreateGlobal(WlCompositor.Interface, 6, (client, version, id) =>
        {
            var compositor = new WlCompositorResource(client, version, id);
            compositor.CreateSurface += (_, e) =>
            {
                if (firstSurface is null)
                {
                    firstSurface = new WlSurfaceResource(client, version, e.Id);
                    firstSurface.Commit += (_, _) => firstCommits++;
                }
                else
                {
                    secondSurface = new WlSurfaceResource(client, version, e.Id);
                    secondSurface.Commit += (_, _) => secondCommits++;
                }
            };
        });

        using var compositor = Bind<WlCompositor>("wl_compositor", 6);
        var surface = compositor.CreateSurface();
        PumpToServer();
        surface.Dispose();
        PumpToServer();

        Assert.NotNull(firstSurface);
        Assert.True(firstSurface!.IsDestroyed);

        using var reused = compositor.CreateSurface();
        reused.Commit();
        PumpToServer();

        Assert.NotNull(secondSurface);
        Assert.Equal(0, firstCommits);
        Assert.Equal(1, secondCommits);
    }

    private static void Churn(WlClient client, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var callback = new WlCallbackResource(client, 1, 0);
            callback.SendDone(7);
            callback.Destroy();
        }
    }
}
