using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wayland;

namespace ShmWindow;

internal static class Program
{
    private const int Width = 640;
    private const int Height = 480;

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create(string name, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(int fd, long length);

    private static int Main()
    {
        using var display = WlDisplay.Connect();
        using var registry = display.GetRegistry();

        WlCompositor? compositor = null;
        WlShm? shm = null;
        XdgWmBase? wmBase = null;

        registry.Global += (_, e) =>
        {
            switch (e.Interface)
            {
                case "wl_compositor":
                    compositor = registry.Bind<WlCompositor>(e.Name, Math.Min(e.Version, 6));
                    break;
                case "wl_shm":
                    shm = registry.Bind<WlShm>(e.Name, 1);
                    break;
                case "xdg_wm_base":
                    wmBase = registry.Bind<XdgWmBase>(e.Name, 1);
                    break;
            }
        };
        display.Roundtrip();

        if (compositor is null || shm is null || wmBase is null)
        {
            Console.Error.WriteLine("Compositor is missing wl_compositor, wl_shm, or xdg_wm_base.");
            return 1;
        }

        wmBase.Ping += (_, e) => wmBase.Pong(e.Serial);

        using var surface = compositor.CreateSurface();
        using var xdgSurface = wmBase.GetXdgSurface(surface);
        using var toplevel = xdgSurface.GetToplevel();
        toplevel.SetTitle("WaylandSharpest shm window");
        toplevel.SetAppId("com.example.WaylandSharpest.ShmWindow");

        var running = true;
        toplevel.Close += (_, _) => running = false;

        var configured = false;
        xdgSurface.Configure += (_, e) =>
        {
            xdgSurface.AckConfigure(e.Serial);
            configured = true;
        };

        surface.Commit();
        while (!configured)
        {
            display.Dispatch();
        }

        using var buffer = CreateGradientBuffer(shm);
        surface.Attach(buffer, 0, 0);
        surface.Commit();

        Console.WriteLine("Window mapped; close it (or Ctrl+C) to exit.");
        while (running)
        {
            display.Dispatch();
        }

        return 0;
    }

    private static WlBuffer CreateGradientBuffer(WlShm shm)
    {
        const int stride = Width * 4;
        const int size = stride * Height;

        var fd = memfd_create("waylandsharpest-shm", 0);
        if (fd < 0)
        {
            throw new InvalidOperationException($"memfd_create failed (errno {Marshal.GetLastPInvokeError()}).");
        }

        if (ftruncate(fd, size) != 0)
        {
            throw new InvalidOperationException($"ftruncate failed (errno {Marshal.GetLastPInvokeError()}).");
        }

        // The compositor maps the fd too, so keep ownership until process exit.
        var handle = new SafeFileHandle(fd, ownsHandle: false);
        using var mapped = MemoryMappedFile.CreateFromFile(handle, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        using var accessor = mapped.CreateViewAccessor(0, size);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var pixel = 0xFF000000u
                    | ((uint)(255 * x / Width) << 16)
                    | ((uint)(255 * y / Height) << 8)
                    | 0x40u;
                accessor.Write((y * stride) + (x * 4), pixel);
            }
        }

        using var pool = shm.CreatePool(fd, size);
        return pool.CreateBuffer(0, Width, Height, stride, WlShm.Format.Argb8888);
    }
}
