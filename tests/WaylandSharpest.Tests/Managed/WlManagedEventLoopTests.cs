using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server;
using WaylandSharpest.Tests.Protocol;
using Xunit;
using static WaylandSharpest.Tests.Managed.ScriptedWireClient;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// The event loop a compositor hangs its own work off: timers, idle callbacks,
/// descriptors and signals, and the back-pressure that stops a server answering
/// a client that has stopped listening.
/// </summary>
public sealed class WlManagedEventLoopTests : IDisposable
{
    private const uint DisplayId = 1;
    private const uint GetRegistryOpcode = 1;
    private const uint RegistryId = 2;
    private const uint FactoryId = 3;

    private readonly WlServerDisplay _display = WlServerDisplay.Create(new ManagedTransport());

    public void Dispose() => _display.Dispose();

    private static void SkipWithoutFileDescriptors() => TestHost.SkipWithoutFdPassingSockets();

    [Fact]
    public void A_timer_does_not_fire_until_it_is_armed()
    {
        var fired = 0;
        var timer = _display.EventLoop.AddTimer(() => fired++);

        _display.EventLoop.Dispatch(0);
        Assert.Equal(0, fired);

        timer.UpdateTimer(1);
        Thread.Sleep(10);
        _display.EventLoop.Dispatch(0);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_timer_fires_once_per_arming()
    {
        var fired = 0;
        var timer = _display.EventLoop.AddTimer(() => fired++);

        timer.UpdateTimer(1);
        Thread.Sleep(10);
        _display.EventLoop.Dispatch(0);
        _display.EventLoop.Dispatch(0);
        Assert.Equal(1, fired);

        timer.UpdateTimer(1);
        Thread.Sleep(10);
        _display.EventLoop.Dispatch(0);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Disarming_a_timer_stops_it()
    {
        var fired = 0;
        var timer = _display.EventLoop.AddTimer(() => fired++);

        timer.UpdateTimer(1);
        timer.UpdateTimer(0);
        Thread.Sleep(10);
        _display.EventLoop.Dispatch(0);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Removing_a_timer_stops_it()
    {
        var fired = 0;
        var timer = _display.EventLoop.AddTimer(() => fired++);

        timer.UpdateTimer(1);
        timer.Remove();
        Thread.Sleep(10);
        _display.EventLoop.Dispatch(0);

        Assert.Equal(0, fired);
        Assert.True(timer.IsRemoved);
    }

    [Fact]
    public void A_pending_timer_bounds_how_long_the_loop_waits()
    {
        var fired = false;
        var timer = _display.EventLoop.AddTimer(() => fired = true);
        timer.UpdateTimer(20);

        // The wait would otherwise block for a second with nothing to report.
        var start = Environment.TickCount64;
        _display.EventLoop.Dispatch(1000);
        var elapsed = Environment.TickCount64 - start;

        Assert.True(fired);
        Assert.True(elapsed < 500, $"The loop waited {elapsed}ms past its timer.");
    }

    [Fact]
    public void An_idle_callback_runs_once_on_the_next_dispatch()
    {
        var fired = 0;
        _display.EventLoop.AddIdle(() => fired++);

        _display.EventLoop.Dispatch(0);
        _display.EventLoop.Dispatch(0);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Idle_callbacks_can_be_run_without_waiting()
    {
        var fired = false;
        _display.EventLoop.AddIdle(() => fired = true);

        _display.EventLoop.DispatchIdle();

        Assert.True(fired);
    }

    [Fact]
    public void A_removed_idle_callback_does_not_run()
    {
        var fired = false;
        var idle = _display.EventLoop.AddIdle(() => fired = true);
        idle.Remove();

        _display.EventLoop.Dispatch(0);

        Assert.False(fired);
    }

    [Fact]
    public void An_idle_callback_keeps_the_loop_from_waiting()
    {
        _display.EventLoop.AddIdle(() => { });

        var start = Environment.TickCount64;
        _display.EventLoop.Dispatch(1000);

        Assert.True(Environment.TickCount64 - start < 500);
    }

    [Fact]
    public void A_watched_descriptor_reports_when_it_becomes_readable()
    {
        SkipWithoutFileDescriptors();

        var (read, write) = CreatePipe();
        try
        {
            var events = WlFdEvents.None;
            var source = _display.EventLoop.AddFd(read, WlFdEvents.Readable, (_, e) => events = e);

            _display.EventLoop.Dispatch(0);
            Assert.Equal(WlFdEvents.None, events);

            WriteByte(write);
            _display.EventLoop.Dispatch(50);

            Assert.True((events & WlFdEvents.Readable) != 0);
            source.Remove();
        }
        finally
        {
            Close(read);
            Close(write);
        }
    }

    [Fact]
    public void A_removed_descriptor_source_stops_reporting()
    {
        SkipWithoutFileDescriptors();

        var (read, write) = CreatePipe();
        try
        {
            var fired = 0;
            var source = _display.EventLoop.AddFd(read, WlFdEvents.Readable, (_, _) => fired++);
            source.Remove();

            WriteByte(write);
            _display.EventLoop.Dispatch(20);

            Assert.Equal(0, fired);
            Assert.True(source.IsRemoved);
        }
        finally
        {
            Close(read);
            Close(write);
        }
    }

    [Fact]
    public void Changing_what_a_descriptor_source_watches_takes_effect()
    {
        SkipWithoutFileDescriptors();

        var (read, write) = CreatePipe();
        try
        {
            var fired = 0;
            var source = _display.EventLoop.AddFd(read, WlFdEvents.Writable, (_, _) => fired++);

            WriteByte(write);
            _display.EventLoop.Dispatch(20);
            Assert.Equal(0, fired);

            source.UpdateFd(WlFdEvents.Readable);
            _display.EventLoop.Dispatch(50);
            Assert.True(fired > 0);

            source.Remove();
        }
        finally
        {
            Close(read);
            Close(write);
        }
    }

    [Fact]
    public void The_loop_offers_a_descriptor_a_host_can_watch()
    {
        SkipWithoutFileDescriptors();
        Assert.True(_display.EventLoop.Fd > 0);
    }

    [Fact]
    public void A_failing_handler_surfaces_from_dispatch()
    {
        _display.EventLoop.AddIdle(() => throw new InvalidOperationException("idle trouble"));

        var thrown = Assert.Throws<WaylandException>(() => _display.EventLoop.Dispatch(0));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
    }

    [Fact]
    public void A_handled_signal_reaches_the_loop_without_ending_the_process()
    {
        SkipWithoutFileDescriptors();

        // The signal is delivered to the thread that registered it, which is
        // the thread whose disposition the loop changed. Running the whole
        // exchange on one dedicated thread makes that deterministic.
        var delivered = 0;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var display = WlServerDisplay.Create(new ManagedTransport());
                var source = display.EventLoop.AddSignal(Sigusr1, _ => delivered++);

                RaiseOnSelf(Sigusr1);
                display.EventLoop.Dispatch(200);

                source.Remove();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The signal was never reported.");
        Assert.Null(failure);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void A_client_that_stops_reading_stops_being_answered()
    {
        var transport = new FakeClientTransport();
        var client = _display.CreateClient(transport);
        var wire = new ScriptedWireClient(transport);

        var makes = 0;
        using var global = _display.CreateGlobal(TestFactory.Interface, 2, (c, version, id) =>
        {
            var factory = new TestFactoryResource(c, version, id);
            factory.MakeChild += (_, e) =>
            {
                makes++;
                _ = new TestChildResource(c, version, e.Id);

                // Something has to be queued for the outgoing side to back up.
                factory.SendReady();
            };
        });

        wire.Send(DisplayId, GetRegistryOpcode, U32(RegistryId));
        _display.EventLoop.Dispatch(0);
        var name = wire.Drain().Single(e => e.Opcode == 0).UInt32At(0);

        wire.Send(RegistryId, 0, Concat(U32(name), Str("test_factory"), U32(2), U32(FactoryId)));
        _display.EventLoop.Dispatch(0);

        // From here the transport refuses every write, so the queue backs up.
        for (var i = 0; i < 32; i++)
        {
            transport.WriteLimits.Enqueue(-1);
        }

        wire.Send(FactoryId, 0, U32(10));
        _display.EventLoop.Dispatch(0);
        _display.EventLoop.Dispatch(0);
        Assert.Equal(1, makes);

        wire.Send(FactoryId, 0, U32(11));
        _display.EventLoop.Dispatch(0);
        Assert.Equal(1, makes);

        // Once the transport takes data again the request is answered.
        transport.WriteLimits.Clear();
        transport.Signal!.NotifyWritable();
        _display.EventLoop.Dispatch(0);
        _display.EventLoop.Dispatch(0);
        Assert.Equal(2, makes);

        Assert.False(client.IsDestroyed);
    }

    [Fact]
    public void A_client_that_leaves_while_backed_up_is_still_reaped()
    {
        var transport = new FakeClientTransport();
        var client = _display.CreateClient(transport);

        for (var i = 0; i < 8; i++)
        {
            transport.WriteLimits.Enqueue(-1);
        }

        // Force something into the outgoing queue, then have the peer vanish.
        var wire = new ScriptedWireClient(transport);
        wire.Send(DisplayId, 0, U32(9));
        _display.EventLoop.Dispatch(0);

        transport.EndOfStream = true;
        transport.Signal!.NotifyReadable();
        _display.EventLoop.Dispatch(0);

        Assert.True(client.IsDestroyed);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public void A_client_beyond_its_outgoing_limit_is_disconnected()
    {
        using var display = WlServerDisplay.Create(
            new ManagedTransport(new ManagedTransportOptions { MaxOutgoingBytes = 64 }));

        var transport = new FakeClientTransport();
        var client = display.CreateClient(transport);
        var wire = new ScriptedWireClient(transport);

        for (var i = 0; i < 32; i++)
        {
            transport.WriteLimits.Enqueue(-1);
        }

        for (var i = 0; i < 16; i++)
        {
            wire.Send(DisplayId, 0, U32((uint)(100 + i)));
        }

        display.EventLoop.Dispatch(0);

        Assert.True(client.IsDestroyed);
    }

    private static int Sigusr1 => OperatingSystem.IsMacOS() ? 30 : 10;

    private static (int Read, int Write) CreatePipe()
    {
        Span<int> fds = stackalloc int[2];
        int result;
        unsafe
        {
            fixed (int* p = fds)
            {
                result = pipe(p);
            }
        }

        Assert.Equal(0, result);
        return (fds[0], fds[1]);
    }

    private static unsafe void WriteByte(int fd)
    {
        byte value = 1;
        Assert.Equal(1, (int)write(fd, &value, 1));
    }

    private static void RaiseOnSelf(int signalNumber) => Assert.Equal(0, pthread_kill(pthread_self(), signalNumber));

    private static void Close(int fd) => close(fd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int pipe(int* fds);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint write(int fd, void* buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint pthread_self();

    [DllImport("libc", SetLastError = true)]
    private static extern int pthread_kill(nint thread, int signalNumber);
}
