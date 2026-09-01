using Wayland;
using Xunit;

namespace WaylandSharpest.Tests;

public class DestructorEventTests : LoopbackHarness
{
    /// <summary>Runs against libwayland.</summary>
    public DestructorEventTests()
    {
    }

    /// <summary>Runs against the given server transport.</summary>
    protected DestructorEventTests(Wayland.Server.IWlServerTransport transport)
        : base(transport)
    {
    }

    [Fact]
    public void A_destructor_event_destroys_the_proxy_after_dispatch()
    {
        var callback = Client.Sync();
        var fired = 0;
        callback.Done += (_, _) =>
        {
            fired++;
            Assert.False(callback.IsDestroyed);
        };
        PumpToClient();
        Assert.Equal(1, fired);
        Assert.True(callback.IsDestroyed);
    }

    [Fact]
    public void A_handler_that_disposes_during_a_destructor_event_is_harmless()
    {
        var callback = Client.Sync();
        callback.Done += (_, _) => callback.Dispose();
        PumpToClient();
        Assert.True(callback.IsDestroyed);
    }

    [Fact]
    public void A_destroyed_wrapper_recycles_into_the_next_created_object()
    {
        var first = Client.Sync();
        var fired = 0;
        first.Done += (_, _) => fired++;
        PumpToClient();
        Assert.True(first.IsDestroyed);

        var second = Client.Sync(first);
        Assert.Same(first, second);
        Assert.False(second.IsDestroyed);

        PumpToClient();
        Assert.Equal(2, fired);
        Assert.True(second.IsDestroyed);
    }

    [Fact]
    public void A_live_wrapper_is_not_recycled()
    {
        var first = Client.Sync();
        var second = Client.Sync(first);
        Assert.NotSame(first, second);

        var firstFired = 0;
        var secondFired = 0;
        first.Done += (_, _) => firstFired++;
        second.Done += (_, _) => secondFired++;
        PumpToClient();
        Assert.Equal(1, firstFired);
        Assert.Equal(1, secondFired);
    }

    [Fact]
    public void A_null_recycle_creates_a_fresh_wrapper()
    {
        var callback = Client.Sync(null);
        var fired = 0;
        callback.Done += (_, _) => fired++;
        PumpToClient();
        Assert.Equal(1, fired);
        Assert.True(callback.IsDestroyed);
    }

}

/// <summary>The same tests, against the managed transport.</summary>
[Trait("Transport", "Managed")]
public sealed class DestructorEventTestsManaged() : DestructorEventTests(new Wayland.Server.ManagedTransport());

[LibWaylandOnly("It asserts the client's recycle path allocates nothing, and the managed loopback server dispatches on the same thread, so its own allocations would land in the count.")]
public sealed class ProxyRecycleAllocationTests : LoopbackHarness
{
    [Fact]
    public void A_recycled_wrapper_allocates_nothing()
    {
        var callback = Client.Sync();
        callback.Done += (_, _) => { };
        PumpToClient();

        for (var i = 0; i < 3; i++)
        {
            callback = Client.Sync(callback);
            PumpToClient();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
        {
            var recycled = Client.Sync(callback);
            Assert.Same(callback, recycled);
            PumpToClient();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
