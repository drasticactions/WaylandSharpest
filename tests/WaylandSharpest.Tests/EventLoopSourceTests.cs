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

    [DllImport("libc")]
    private static extern int close(int fd);

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
}
