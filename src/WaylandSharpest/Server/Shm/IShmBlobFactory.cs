using System.Runtime.InteropServices;

namespace Wayland.Server.Shm;

/// <summary>
/// An immutable shared-memory blob the compositor produced to send in an
/// event's fd argument — a keymap, a dmabuf format table, an ICC profile.
/// <see cref="FdSlot"/> stays valid across any number of event sends, because
/// the transport duplicates at marshal exactly as libwayland does for fds.
/// Dispose releases the compositor's reference.
/// </summary>
public interface IShmBlob : IDisposable
{
    /// <summary>The value to place in an event's fd argument.</summary>
    int FdSlot { get; }

    /// <summary>The blob's length in bytes.</summary>
    uint Size { get; }
}

/// <summary>
/// Produces <see cref="IShmBlob"/>s for the compositor's event-direction fds.
/// One implementation per fd-slot family, selected through
/// <see cref="ShmBlobs.ForClient"/>: a client whose fd-slots are kernel file
/// descriptors gets an anonymous file, a token client gets a slot minted on a
/// host-owned region.
/// </summary>
public interface IShmBlobFactory
{
    /// <summary>Creates a blob holding a copy of <paramref name="content"/>.</summary>
    IShmBlob Create(string debugName, ReadOnlySpan<byte> content);
}

/// <summary>Selects the <see cref="IShmBlobFactory"/> for a client.</summary>
public static class ShmBlobs
{
    /// <summary>
    /// The factory matching <paramref name="client"/>: its token table when it
    /// has one, otherwise the host's anonymous-file mechanism. One display can
    /// serve clients of both kinds, so this is chosen per client and never
    /// cached across them.
    /// </summary>
    public static IShmBlobFactory ForClient(WlClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return ForFdSlots(client.FdSlots);
    }

    /// <summary>
    /// The factory for fd-slots minted from <paramref name="slots"/>, or for
    /// kernel file descriptors when it is null.
    /// </summary>
    public static IShmBlobFactory ForFdSlots(IFdSlotTable? slots)
    {
        if (slots is not null)
        {
            return new TokenBlobFactory(slots);
        }

        if (OperatingSystem.IsLinux())
        {
            return new MemfdBlobFactory();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new AnonymousFileBlobFactory();
        }

        throw new PlatformNotSupportedException(
            "No anonymous-file mechanism on this platform; the transport must supply a token table.");
    }
}

/// <summary>The Linux blob factory: a sealed-size <c>memfd</c> per blob.</summary>
public sealed class MemfdBlobFactory : IShmBlobFactory
{
    /// <inheritdoc/>
    public unsafe IShmBlob Create(string debugName, ReadOnlySpan<byte> content)
    {
        ArgumentOutOfRangeException.ThrowIfZero(content.Length);
        var fd = memfd_create(debugName, 1);
        if (fd < 0 || ftruncate(fd, content.Length) != 0)
        {
            if (fd >= 0)
            {
                close(fd);
            }

            throw new WaylandException($"memfd '{debugName}' creation failed: errno {Marshal.GetLastPInvokeError()}");
        }

        var map = mmap(0, (nuint)content.Length, 3, 1, fd, 0);
        if (map == -1)
        {
            close(fd);
            throw new WaylandException($"memfd '{debugName}' mmap failed: errno {Marshal.GetLastPInvokeError()}");
        }

        content.CopyTo(new Span<byte>((void*)map, content.Length));
        munmap(map, (nuint)content.Length);
        return new FdBlob(fd, (uint)content.Length, static fd => close(fd));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(int fd, long length);

    [DllImport("libc", SetLastError = true)]
    private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(nint addr, nuint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}

/// <summary>
/// The macOS blob factory: an unlinked temporary file per blob, the Darwin
/// analog of a <c>memfd</c>.
/// </summary>
public sealed class AnonymousFileBlobFactory : IShmBlobFactory
{
    private const ulong Fioclex = 0x20006601; // _IO('f', 1): set close-on-exec.

    /// <inheritdoc/>
    public unsafe IShmBlob Create(string debugName, ReadOnlySpan<byte> content)
    {
        ArgumentOutOfRangeException.ThrowIfZero(content.Length);
        var template = System.Text.Encoding.UTF8.GetBytes(
            Path.Combine(Path.GetTempPath(), $"{debugName}-XXXXXX\0"));
        int fd;
        string path;
        fixed (byte* p = template)
        {
            fd = mkstemp(p);
            if (fd < 0)
            {
                throw new WaylandException($"mkstemp for '{debugName}' failed: errno {Marshal.GetLastPInvokeError()}");
            }

            path = System.Text.Encoding.UTF8.GetString(template, 0, template.Length - 1);
        }

        ioctl(fd, Fioclex);
        unlink(path);

        fixed (byte* bytes = content)
        {
            var offset = 0;
            while (offset < content.Length)
            {
                var wrote = (int)pwrite(fd, bytes + offset, (nuint)(content.Length - offset), offset);
                if (wrote <= 0)
                {
                    close(fd);
                    throw new WaylandException($"write to '{debugName}' failed: errno {Marshal.GetLastPInvokeError()}");
                }

                offset += wrote;
            }
        }

        return new FdBlob(fd, (uint)content.Length, static fd => close(fd));
    }

    [DllImport("libSystem", SetLastError = true)]
    private static extern unsafe int mkstemp(byte* template);

    [DllImport("libSystem", SetLastError = true)]
    private static extern int unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libSystem", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl(int fd, ulong request);

    [DllImport("libSystem", SetLastError = true)]
    private static extern unsafe nint pwrite(int fd, byte* buf, nuint count, long offset);

    [DllImport("libSystem", SetLastError = true)]
    private static extern int close(int fd);
}

/// <summary>
/// The token-transport blob factory: each blob is a host-owned
/// <see cref="SharedMemoryRegion"/> addressed by a minted slot.
/// </summary>
public sealed class TokenBlobFactory : IShmBlobFactory
{
    private readonly IFdSlotTable _slots;

    /// <summary>Creates the factory over its transport's token table.</summary>
    public TokenBlobFactory(IFdSlotTable slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        _slots = slots;
    }

    /// <inheritdoc/>
    public IShmBlob Create(string debugName, ReadOnlySpan<byte> content)
    {
        ArgumentOutOfRangeException.ThrowIfZero(content.Length);
        var region = new SharedMemoryRegion(content.Length);
        content.CopyTo(region.Span);
        var slot = _slots.Mint(region);
        var slots = _slots;
        return new FdBlob(slot, (uint)content.Length, slot => slots.Close(slot));
    }
}

internal sealed class FdBlob : IShmBlob
{
    private readonly Action<int> _release;
    private int _fdSlot;

    internal FdBlob(int fdSlot, uint size, Action<int> release)
    {
        _fdSlot = fdSlot;
        Size = size;
        _release = release;
    }

    public int FdSlot => _fdSlot;

    public uint Size { get; }

    public void Dispose()
    {
        if (_fdSlot >= 0)
        {
            _release(_fdSlot);
            _fdSlot = -1;
        }
    }
}
