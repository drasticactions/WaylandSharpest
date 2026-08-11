using System.Runtime.InteropServices;
using Wayland.Server;
using Wayland.Server.Shm;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Exercises the event-direction blob factories.
/// </summary>
public sealed class ShmBlobFactoryTests
{
    private static byte[] Content()
    {
        var bytes = new byte[512];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 7);
        }

        return bytes;
    }

    [Fact]
    public unsafe void The_platform_factory_round_trips_content()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("No anonymous-file mechanism on this platform.");
        }

        var factory = ShmBlobs.ForTransport(LibWaylandTransport.Instance);
        var content = Content();
        using var blob = factory.Create("waylandsharpest-blob-test", content);
        Assert.Equal((uint)content.Length, blob.Size);

        var read = new byte[content.Length];
        fixed (byte* p = read)
        {
            var got = OperatingSystem.IsLinux()
                ? LinuxPread(blob.FdSlot, p, (nuint)read.Length, 0)
                : MacPread(blob.FdSlot, p, (nuint)read.Length, 0);
            Assert.Equal(read.Length, (int)got);
        }

        Assert.Equal(content, read);
    }

    [Fact]
    public void The_token_factory_mints_a_slot_on_a_region()
    {
        var table = new FdSlotTable();
        var factory = new TokenBlobFactory(table);
        var content = Content();

        var blob = factory.Create("waylandsharpest-blob-test", content);
        Assert.Equal(1, table.Count);

        var region = table.Resolve<SharedMemoryRegion>(blob.FdSlot);
        Assert.True(region.Span.SequenceEqual(content));

        var dup = table.Duplicate(blob.FdSlot);
        blob.Dispose();
        Assert.Equal(1, table.Count);
        Assert.True(table.Resolve<SharedMemoryRegion>(dup).Span.SequenceEqual(content));
        table.Close(dup);
        Assert.Equal(0, table.Count);
        Assert.Throws<ObjectDisposedException>(() => region.Span[0]);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var table = new FdSlotTable();
        var blob = new TokenBlobFactory(table).Create("waylandsharpest-blob-test", Content());
        blob.Dispose();
        blob.Dispose();
        Assert.Equal(0, table.Count);
    }

    [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static extern unsafe nint LinuxPread(int fd, byte* buf, nuint count, long offset);

    [DllImport("libSystem", EntryPoint = "pread", SetLastError = true)]
    private static extern unsafe nint MacPread(int fd, byte* buf, nuint count, long offset);
}
