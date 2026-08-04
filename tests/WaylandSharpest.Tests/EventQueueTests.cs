using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Event queues are what let a second thread service its own objects without
/// stealing the main thread's events. Only the last test uses a real thread.
/// </summary>
public sealed class EventQueueTests : LoopbackHarness
{
    private WlSeatResource? _serverSeat;

    private WlGlobal PublishSeat() =>
        Server.CreateGlobal(WlSeat.Interface, 5, (client, version, id) =>
            _serverSeat = new WlSeatResource(client, version, id));

    [Fact]
    public void Objects_created_through_a_wrapper_join_its_queue()
    {
        using var global = PublishSeat();
        using var queue = Client.CreateQueue("render");

        using var registry = Client.GetRegistry();
        uint seatName = 0;
        registry.Global += (_, e) =>
        {
            if (e.Interface == "wl_seat")
            {
                seatName = e.Name;
            }
        };
        PumpToClient();
        Assert.NotEqual(0u, seatName);
        Assert.Null(registry.Queue);

        using var wrapper = registry.CreateWrapper<WlRegistry>(queue);
        Assert.True(wrapper.IsWrapper);
        Assert.Same(queue, wrapper.Queue);

        // Born on the wrapper's queue, with no window between creation and
        // SetQueue for an event to slip through.
        using var seat = wrapper.Bind<WlSeat>(seatName, 5);
        Assert.Same(queue, seat.Queue);

        // The wrapper counts too: it points at the queue and creates objects there.
        Assert.Equal(2, queue.AssignedProxyCount);

        var names = new List<string>();
        seat.Name += (_, e) => names.Add(e.Name);

        PumpToServer();
        Assert.NotNull(_serverSeat);
        _serverSeat!.SendName("seat0");
        Server.FlushClients();
        Assert.True(Client.TryReadEvents(100));

        // The default queue must not deliver this object's events.
        Client.DispatchPending();
        Assert.Empty(names);

        queue.DispatchPending();
        Assert.Equal(["seat0"], names);
    }

    [Fact]
    public void Disposing_a_queue_with_live_proxies_throws()
    {
        using var global = PublishSeat();
        var queue = Client.CreateQueue("busy");
        using var seat = Bind<WlSeat>("wl_seat", 5);
        seat.SetQueue(queue);

        Assert.Equal(1, queue.AssignedProxyCount);
        var ex = Assert.Throws<InvalidOperationException>(() => queue.Dispose());
        Assert.Contains("1 proxies", ex.Message);
        Assert.False(queue.IsDisposed);

        // Moving the proxy off releases the queue.
        seat.SetQueue(null);
        Assert.Equal(0, queue.AssignedProxyCount);
        queue.Dispose();
        Assert.True(queue.IsDisposed);
    }

    [Fact]
    public void Destroying_a_proxy_releases_its_queue()
    {
        using var global = PublishSeat();
        using var queue = Client.CreateQueue();
        var seat = Bind<WlSeat>("wl_seat", 5);
        seat.SetQueue(queue);
        Assert.Equal(1, queue.AssignedProxyCount);

        seat.Dispose();
        Assert.Equal(0, queue.AssignedProxyCount);
    }

    [Fact]
    public void Wrapper_dispose_does_not_destroy_the_object()
    {
        // wl_proxy_destroy on a wrapper aborts the process, and a generated
        // DisposeCore would send a real destructor request first; this is the
        // regression test for both.
        using var global = PublishSeat();
        using var queue = Client.CreateQueue();
        using var seat = Bind<WlSeat>("wl_seat", 5);

        var wrapper = seat.CreateWrapper<WlSeat>(queue);
        Assert.Equal(1, queue.AssignedProxyCount);

        wrapper.Dispose();
        Assert.True(wrapper.IsDestroyed);
        Assert.Equal(0, queue.AssignedProxyCount);

        // The underlying object is untouched and still usable.
        Assert.False(seat.IsDestroyed);
        using var pointer = seat.GetPointer();
        PumpToServer();
        Assert.NotNull(_serverSeat);
        Assert.False(_serverSeat!.IsDestroyed);
    }

    [Fact]
    public void A_wrapper_shares_the_wrapped_objects_identity()
    {
        using var global = PublishSeat();
        using var queue = Client.CreateQueue();
        using var seat = Bind<WlSeat>("wl_seat", 5);
        using var wrapper = seat.CreateWrapper<WlSeat>(queue);

        Assert.Equal(seat.Id, wrapper.Id);
        Assert.Equal(seat.Version, wrapper.Version);
        Assert.NotEqual(seat.RawHandle, wrapper.RawHandle);
    }

    [Fact]
    public void Queue_dispatch_rethrows_only_its_own_handler_exception()
    {
        using var global = PublishSeat();
        using var a = Client.CreateQueue("a");
        using var b = Client.CreateQueue("b");
        using var seat = Bind<WlSeat>("wl_seat", 5);
        seat.SetQueue(a);
        seat.Name += (_, _) => throw new InvalidOperationException("queue a boom");

        PumpToServer();
        Assert.NotNull(_serverSeat);
        _serverSeat!.SendName("seat0");
        Server.FlushClients();
        Assert.True(Client.TryReadEvents(100));

        // The exception belongs to queue a and must not surface elsewhere.
        b.DispatchPending();
        Client.DispatchPending();

        var ex = Assert.Throws<WaylandException>(() => a.DispatchPending());
        Assert.IsType<InvalidOperationException>(ex.InnerException);

        // Cleared once observed.
        a.DispatchPending();
    }

    [Fact]
    public void Prepare_read_returns_false_when_events_are_pending()
    {
        using var global = PublishSeat();
        using var registry = Client.GetRegistry();
        Client.Flush();
        Server.EventLoop.Dispatch(100);
        Server.FlushClients();

        // Nothing queued yet, so a read can be prepared; resolve it by reading.
        Assert.True(Client.TryPrepareRead());
        Client.ReadEvents();

        // Now the events are queued and undispatched: the EAGAIN path.
        Assert.False(Client.TryPrepareRead());

        Client.DispatchPending();
        Assert.True(Client.TryPrepareRead());
        Client.CancelRead();
    }

    [Fact]
    public void Try_read_events_reports_a_timeout_without_blocking_forever()
    {
        using var global = PublishSeat();
        Assert.False(Client.TryReadEvents(10));
    }

    [Fact]
    public void Dispatch_with_a_timeout_returns_when_nothing_arrives()
    {
        using var global = PublishSeat();
        Client.Dispatch(10);
    }

    [Fact]
    public void Named_queues_report_their_name_where_libwayland_supports_it()
    {
        using var queue = Client.CreateQueue("compositor-events");

        // Naming is 1.23+; on an older library the name is simply absent.
        Assert.True(queue.Name is null or "compositor-events");
    }

    [Fact]
    public void Second_thread_dispatches_its_own_queue()
    {
        using var global = PublishSeat();
        using var queue = Client.CreateQueue("worker");
        using var seat = Bind<WlSeat>("wl_seat", 5);
        seat.SetQueue(queue);

        var mainThread = Environment.CurrentManagedThreadId;
        var handlerThread = 0;
        string? name = null;
        using var received = new ManualResetEventSlim();
        seat.Name += (_, e) =>
        {
            name = e.Name;
            handlerThread = Environment.CurrentManagedThreadId;
            received.Set();
        };

        PumpToServer();
        Assert.NotNull(_serverSeat);

        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                queue.Dispatch();
            }
            catch (Exception ex)
            {
                workerFailure = ex;
                received.Set();
            }
        });
        worker.Start();

        _serverSeat!.SendName("seat0");
        Server.FlushClients();

        Assert.True(received.Wait(TimeSpan.FromSeconds(10)), "the worker thread never dispatched the event");
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "the worker thread did not exit");

        Assert.Null(workerFailure);
        Assert.Equal("seat0", name);
        Assert.NotEqual(mainThread, handlerThread);
    }
}
