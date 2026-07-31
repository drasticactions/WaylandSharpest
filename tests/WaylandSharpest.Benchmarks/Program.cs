using System.Diagnostics;
using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server;

internal static class Program
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int socketpair(int domain, int type, int protocol, int* sv);

    private static int Main(string[] args)
    {
        var iterations = args.Length > 0 ? int.Parse(args[0]) : 1_000_000;
        const int batch = 100;

        var server = WlServerDisplay.Create();
        int fd0, fd1;
        unsafe
        {
            var fds = stackalloc int[2];
            if (socketpair(AF_UNIX, SOCK_STREAM, 0, fds) != 0)
            {
                Console.Error.WriteLine("socketpair failed");
                return 1;
            }

            fd0 = fds[0];
            fd1 = fds[1];
        }

        server.CreateClient(fd0);
        var client = WlDisplay.ConnectToFd(fd1);

        var result = Run(server, client, iterations, batch);

        client.Dispose();
        server.Dispose();
        return result;
    }

    private static int Run(WlServerDisplay server, WlDisplay client, int iterations, int batch)
    {
        var damageCount = 0L;
        WlSeatResource? serverSeat = null;
        using var compositorGlobal = server.CreateGlobal(WlCompositor.Interface, 6, (c, version, id) =>
        {
            var compositor = new WlCompositorResource(c, version, id);
            compositor.CreateSurface += (_, e) =>
            {
                var surface = new WlSurfaceResource(compositor.Client, compositor.Version, e.Id);
                surface.Damage += (_, _) => damageCount++;
            };
        });
        using var seatGlobal = server.CreateGlobal(WlSeat.Interface, 5, (c, version, id) =>
        {
            serverSeat = new WlSeatResource(c, version, id);
        });

        using var registry = client.GetRegistry();
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
        client.Flush();
        server.EventLoop.Dispatch(100);
        server.FlushClients();
        client.Dispatch();

        using var compositor = registry.Bind<WlCompositor>(compositorName, 6);
        using var seat = registry.Bind<WlSeat>(seatName, 5);
        var capsCount = 0L;
        seat.Capabilities += (_, _) => capsCount++;
        var surface = compositor.CreateSurface();
        client.Flush();
        server.EventLoop.Dispatch(100);

        void InboundBody(int n)
        {
            for (var i = 0; i < n; i++)
            {
                surface.Damage(0, 0, 1, 1);
                if (i % batch == batch - 1)
                {
                    client.Flush();
                    server.EventLoop.Dispatch(0);
                }
            }
        }

        void InboundDrain()
        {
            client.Flush();
            server.EventLoop.Dispatch(0);
        }

        RunPass("inbound  (wl_surface.damage)", warmup: true, iterations / 10, () => damageCount, InboundBody, InboundDrain);
        RunPass("inbound  (wl_surface.damage)", warmup: false, iterations, () => damageCount, InboundBody, InboundDrain);

        if (serverSeat is null)
        {
            Console.Error.WriteLine("seat was not bound");
            return 1;
        }

        void OutboundBody(int n)
        {
            for (var i = 0; i < n; i++)
            {
                serverSeat.SendCapabilities(WlSeat.Capability.Pointer);
                if (i % batch == batch - 1)
                {
                    server.FlushClients();
                    client.Dispatch();
                }
            }
        }

        void OutboundDrain()
        {
            server.FlushClients();
            client.Dispatch();
        }

        RunPass("outbound (wl_seat.capabilities)", warmup: true, iterations / 10, () => capsCount, OutboundBody, OutboundDrain);
        RunPass("outbound (wl_seat.capabilities)", warmup: false, iterations, () => capsCount, OutboundBody, OutboundDrain);

        surface.Dispose();
        client.Flush();
        server.EventLoop.Dispatch(0);
        return 0;
    }

    private static void RunPass(string name, bool warmup, int n, Func<long> counter, Action<int> body, Action drain)
    {
        var before = counter();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        body(n);
        while (counter() - before < n)
        {
            drain();
        }

        sw.Stop();
        var alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        if (!warmup)
        {
            var rate = n / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"{name}: {n:N0} msgs in {sw.Elapsed.TotalMilliseconds:F1} ms = {rate:N0} msg/s, {(double)alloc / n:F2} B/msg");
        }
    }
}
