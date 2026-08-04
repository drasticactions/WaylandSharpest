using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server;
using Xunit;

namespace WaylandSharpest.Tests;

public sealed class EventLoopSourceTests : IDisposable
{
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int pipe(int* fds);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint write(int fd, byte* buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint read(int fd, byte* buf, nuint count);

    [DllImport("libc")]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int raise(int signalNumber);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int poll(PollFd* fds, nuint count, int timeoutMs);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private const short POLLIN = 1;
    private const int SIGUSR1 = 10;

    private static unsafe bool IsReadable(int fd)
    {
        var entry = new PollFd { Fd = fd, Events = POLLIN };
        Assert.True(poll(&entry, 1, 0) >= 0);
        return (entry.Revents & POLLIN) != 0;
    }

    private readonly WlServerDisplay _display = WlServerDisplay.Create();

    public void Dispose() => _display.Dispose();

    [Fact]
    public void Idle_callback_runs_once_before_the_loop_sleeps()
    {
        var fired = 0;
        var source = _display.EventLoop.AddIdle(() => fired++);
        Assert.False(source.IsRemoved);

        _display.EventLoop.Dispatch(0);
        Assert.Equal(1, fired);
        Assert.True(source.IsRemoved);

        _display.EventLoop.Dispatch(0);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Idle_callback_can_be_cancelled()
    {
        var fired = 0;
        var source = _display.EventLoop.AddIdle(() => fired++);
        source.Remove();
        _display.EventLoop.Dispatch(0);
        Assert.Equal(0, fired);
        Assert.True(source.IsRemoved);
    }

    [Fact]
    public void Timer_fires_after_being_armed()
    {
        var fired = 0;
        var source = _display.EventLoop.AddTimer(() => fired++);

        _display.EventLoop.Dispatch(0);
        Assert.Equal(0, fired);

        source.UpdateTimer(1);
        _display.EventLoop.Dispatch(50);
        Assert.Equal(1, fired);

        // One-shot until re-armed.
        _display.EventLoop.Dispatch(10);
        Assert.Equal(1, fired);

        source.Remove();
        Assert.True(source.IsRemoved);
    }

    [Fact]
    public unsafe void Fd_source_reports_readiness()
    {
        var fds = stackalloc int[2];
        Assert.Equal(0, pipe(fds));

        var events = new List<WlFdEvents>();
        var source = _display.EventLoop.AddFd(fds[0], WlFdEvents.Readable, (fd, e) =>
        {
            Assert.Equal(fds[0], fd);
            events.Add(e);
        });

        _display.EventLoop.Dispatch(0);
        Assert.Empty(events);

        byte b = 42;
        Assert.Equal(1, write(fds[1], &b, 1));
        _display.EventLoop.Dispatch(50);
        Assert.Single(events);
        Assert.True(events[0].HasFlag(WlFdEvents.Readable));

        source.Remove();
        close(fds[0]);
        close(fds[1]);
    }

    [Fact]
    public void Source_callback_exceptions_surface_on_dispatch()
    {
        _display.EventLoop.AddIdle(() => throw new InvalidOperationException("idle boom"));
        var ex = Assert.Throws<WaylandException>(() => _display.EventLoop.Dispatch(0));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public unsafe void Event_loop_fd_becomes_readable_when_work_is_pending()
    {
        var loopFd = _display.EventLoop.Fd;
        Assert.True(loopFd >= 0);
        Assert.False(IsReadable(loopFd));

        var fds = stackalloc int[2];
        Assert.Equal(0, pipe(fds));
        var fired = false;
        var readFd = fds[0];
        var source = _display.EventLoop.AddFd(readFd, WlFdEvents.Readable, (_, _) =>
        {
            fired = true;

            // Drain, or the pipe stays readable and so does the loop's epoll fd.
            byte sink;
            read(readFd, &sink, 1);
        });

        try
        {
            byte b = 1;
            Assert.Equal(1, write(fds[1], &b, 1));

            // The epoll fd is now readable, which is the signal a host loop polls for.
            Assert.True(IsReadable(loopFd));

            _display.EventLoop.Dispatch(0);
            Assert.True(fired);
            Assert.False(IsReadable(loopFd));
        }
        finally
        {
            source.Remove();
            close(fds[0]);
            close(fds[1]);
        }
    }

    [Fact]
    public void Signal_source_fires_on_the_loop_thread()
    {
        var received = new List<int>();
        var loopThread = Environment.CurrentManagedThreadId;
        var handlerThread = 0;
        var source = _display.EventLoop.AddSignal(SIGUSR1, signal =>
        {
            received.Add(signal);
            handlerThread = Environment.CurrentManagedThreadId;
        });

        try
        {
            Assert.Equal(0, raise(SIGUSR1));
            _display.EventLoop.Dispatch(100);

            Assert.Equal([SIGUSR1], received);
            Assert.Equal(loopThread, handlerThread);
        }
        finally
        {
            source.Remove();
        }
    }

    [Fact]
    public void Signal_source_stops_after_removal()
    {
        var fired = 0;
        var source = _display.EventLoop.AddSignal(SIGUSR1, _ => fired++);
        source.Remove();
        Assert.True(source.IsRemoved);

        Assert.Equal(0, raise(SIGUSR1));
        _display.EventLoop.Dispatch(10);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Dispatch_idle_runs_idle_callbacks_without_waiting()
    {
        var fired = 0;
        _display.EventLoop.AddIdle(() => fired++);

        _display.EventLoop.DispatchIdle();
        Assert.Equal(1, fired);

        _display.EventLoop.DispatchIdle();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Dispatch_idle_rethrows_handler_exceptions()
    {
        _display.EventLoop.AddIdle(() => throw new InvalidOperationException("idle boom"));
        var ex = Assert.Throws<WaylandException>(() => _display.EventLoop.DispatchIdle());
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}
