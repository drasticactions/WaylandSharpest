using System.Runtime.InteropServices;
using Wayland.Server.Shm;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Exercises mmap-backed <see cref="ISharedMemory"/>.
/// </summary>
[CollectionDefinition("shared-memory-fd-table", DisableParallelization = true)]
public sealed class SharedMemoryFdTableCollection;

[Collection("shared-memory-fd-table")]
public sealed class SharedMemoryTests : IDisposable
{
    private const int PageSize = 4096;

    public void Dispose()
    {
        foreach (var fd in _ownedFds)
        {
            Posix.Close(fd);
        }
    }

    [Fact]
    public void Map_reads_the_backing_file()
    {
        AssertMmapPlatform();
        var shm = SharedMemory.CreateForPlatform();
        var fd = CreateBackingFile(PageSize);
        FillPattern(fd, 0, PageSize, seed: 7);

        using var mapping = shm.Map(fd, PageSize);
        Assert.Equal(PageSize, mapping.Size);
        Assert.Equal(PageSize, mapping.Span.Length);
        AssertPattern(mapping.Span, seed: 7);

        Assert.True(mapping.IsWritable);
        unsafe
        {
            *(byte*)mapping.Address = 0xC3;
        }

        Assert.Equal(0xC3, mapping.Span[0]);
    }

    [Fact]
    public void A_write_sealed_pool_maps_read_only()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("memfd write seals are Linux-only.");
        }

        var shm = SharedMemory.CreateForPlatform();
        var fd = Posix.CreateSealableMemfd(PageSize);

        using var mapping = shm.Map(fd, PageSize);
        Assert.False(mapping.IsWritable);
        Assert.Equal(PageSize, mapping.Span.Length);
    }

    [Fact]
    public void Map_consumes_the_fd_when_the_size_is_rejected()
    {
        AssertMmapPlatform();
        var shm = SharedMemory.CreateForPlatform();
        var fd = CreateBackingFile(PageSize);

        Assert.Throws<ArgumentOutOfRangeException>(() => shm.Map(fd, 0));
        Assert.False(IsFdOpen(fd));
    }

    [Fact]
    public void Map_consumes_the_fd_when_mmap_fails()
    {
        AssertMmapPlatform();
        var shm = SharedMemory.CreateForPlatform();

        // A pipe is not mappable, so mmap fails while the fd stays exclusively
        // ours — a pre-closed fd would race a concurrent allocation and hand
        // Map somebody else's descriptor to close.
        var (readEnd, writeEnd) = Posix.Pipe();
        Assert.Throws<Wayland.WaylandException>(() => shm.Map(readEnd, PageSize));
        Assert.False(IsFdOpen(readEnd));
        Posix.Close(writeEnd);
    }

    [Fact]
    public void Remap_keeps_the_old_generation_readable_until_disposed()
    {
        AssertMmapPlatform();
        var shm = SharedMemory.CreateForPlatform();
        var fd = CreateBackingFile(PageSize);
        FillPattern(fd, 0, PageSize, seed: 3);

        var first = shm.Map(fd, PageSize);
        Posix.Truncate(BackingFdOf(first), 2 * PageSize);
        var second = first.Remap(2 * PageSize);

        AssertPattern(first.Span, seed: 3);
        Assert.Equal(2 * PageSize, second.Size);
        AssertPattern(second.Span[..PageSize], seed: 3);

        first.Dispose();
        AssertPattern(second.Span[..PageSize], seed: 3);
        second.Dispose();
    }

    [Fact]
    public void Remap_survives_disposing_the_new_generation_first()
    {
        AssertMmapPlatform();
        var shm = SharedMemory.CreateForPlatform();
        var fd = CreateBackingFile(PageSize);
        FillPattern(fd, 0, PageSize, seed: 11);

        var first = shm.Map(fd, PageSize);
        Posix.Truncate(BackingFdOf(first), 2 * PageSize);
        var second = first.Remap(2 * PageSize);

        second.Dispose();
        AssertPattern(first.Span, seed: 11);
        first.Dispose();
    }

    [Fact]
    public void Disposed_mapping_refuses_access()
    {
        AssertMmapPlatform();
        var shm = SharedMemory.CreateForPlatform();
        var fd = CreateBackingFile(PageSize);

        var mapping = shm.Map(fd, PageSize);
        mapping.Dispose();
        mapping.Dispose();
        Assert.Throws<ObjectDisposedException>(() => mapping.Address);
        Assert.Throws<ObjectDisposedException>(() => mapping.Remap(2 * PageSize));
    }

    [Fact]
    public unsafe void TryCopyRows_copies_contiguous_and_strided()
    {
        AssertMmapPlatform();
        var shm = SharedMemory.CreateForPlatform();
        var fd = CreateBackingFile(PageSize);
        FillPattern(fd, 0, PageSize, seed: 5);

        using var mapping = shm.Map(fd, PageSize);
        const int rows = 16;
        const int rowBytes = 64;
        const int sourceStride = 128;

        var contiguous = new byte[rows * rowBytes];
        fixed (byte* dst = contiguous)
        {
            Assert.True(shm.TryCopyRows((nint)dst, rowBytes, mapping.Address, rowBytes, rowBytes, rows));
            Assert.True(mapping.Span[..(rows * rowBytes)].SequenceEqual(contiguous));
        }

        var strided = new byte[rows * rowBytes];
        fixed (byte* dst = strided)
        {
            Assert.True(shm.TryCopyRows((nint)dst, rowBytes, mapping.Address, sourceStride, rowBytes, rows));
        }

        for (var row = 0; row < rows; row++)
        {
            Assert.True(mapping.Span.Slice(row * sourceStride, rowBytes)
                .SequenceEqual(strided.AsSpan(row * rowBytes, rowBytes)));
        }
    }

    [Fact]
    public unsafe void TryCopyRows_survives_a_pool_truncated_under_the_mapping()
    {
        AssertMmapPlatform();
        var shm = SharedMemory.CreateForPlatform();
        var fd = CreateBackingFile(2 * PageSize);
        FillPattern(fd, 0, 2 * PageSize, seed: 9);

        using var mapping = shm.Map(fd, 2 * PageSize);
        Posix.Truncate(BackingFdOf(mapping), PageSize);

        // The load-bearing assertion is that this call returns at all: a direct
        // read of the truncated tail would raise SIGBUS on Linux and kill the
        // process. Linux reports the unbacked page as a failed copy. Darwin
        // zero-fills mapped pages beyond a shrunk file's EOF instead of
        // faulting, so the copy succeeds there and there is no signal to dodge.
        var destination = new byte[2 * PageSize];
        bool copied;
        fixed (byte* dst = destination)
        {
            copied = shm.TryCopyRows((nint)dst, PageSize, mapping.Address, PageSize, PageSize, 2);
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.False(copied);
        }
        else
        {
            Assert.True(copied);
        }
    }

    [Fact]
    public void The_factory_refuses_hosts_without_kernel_fds()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Assert.IsAssignableFrom<ISharedMemory>(SharedMemory.CreateForPlatform());
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(SharedMemory.CreateForPlatform);
        }
    }

    private static void AssertMmapPlatform()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("mmap-backed shared memory exists only on Linux and macOS.");
        }
    }

    // The mapping under test duplicates its fd internally, so tests that
    // truncate the backing file keep their own descriptor: CreateBackingFile
    // dups the fd it hands to Map and remembers the original per test instance.
    private readonly List<int> _ownedFds = [];

    private int CreateBackingFile(int size)
    {
        var fd = Posix.CreateUnlinkedFile();
        Posix.Truncate(fd, size);
        var forMapping = Posix.Dup(fd);
        _ownedFds.Add(fd);
        return forMapping;
    }

    private int BackingFdOf(IMappedMemory _) => _ownedFds[^1];

    private static void FillPattern(int fd, long offset, int count, byte seed)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            bytes[i] = (byte)(seed + i * 31);
        }

        Posix.WriteAt(fd, offset, bytes);
    }

    private static void AssertPattern(ReadOnlySpan<byte> span, byte seed)
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != (byte)(seed + i * 31))
            {
                Assert.Fail($"pattern mismatch at byte {i}");
            }
        }
    }

    private static bool IsFdOpen(int fd) => Posix.GetFdFlags(fd) >= 0;

    private static unsafe class Posix
    {
        private const int FGetFd = 1;

        public static int CreateUnlinkedFile()
        {
            if (OperatingSystem.IsLinux())
            {
                var fd = LinuxMemfd("waylandsharpest-shm-test", 1 );
                if (fd < 0)
                {
                    throw new InvalidOperationException($"memfd_create failed: errno {Marshal.GetLastPInvokeError()}");
                }

                return fd;
            }

            var template = System.Text.Encoding.UTF8.GetBytes(
                Path.Combine(Path.GetTempPath(), "waylandsharpest-shm-test-XXXXXX\0"));
            fixed (byte* p = template)
            {
                var fd = MacMkstemp(p);
                if (fd < 0)
                {
                    throw new InvalidOperationException($"mkstemp failed: errno {Marshal.GetLastPInvokeError()}");
                }

                var path = System.Text.Encoding.UTF8.GetString(template, 0, template.Length - 1);
                MacUnlink(path);
                return fd;
            }
        }

        public static void Truncate(int fd, long size)
        {
            var rc = OperatingSystem.IsLinux() ? LinuxFtruncate(fd, size) : MacFtruncate(fd, size);
            if (rc != 0)
            {
                throw new InvalidOperationException($"ftruncate failed: errno {Marshal.GetLastPInvokeError()}");
            }
        }

        public static int Dup(int fd)
        {
            var dup = OperatingSystem.IsLinux() ? LinuxDup(fd) : MacDup(fd);
            if (dup < 0)
            {
                throw new InvalidOperationException($"dup failed: errno {Marshal.GetLastPInvokeError()}");
            }

            return dup;
        }

        public static void WriteAt(int fd, long offset, ReadOnlySpan<byte> bytes)
        {
            fixed (byte* p = bytes)
            {
                var wrote = OperatingSystem.IsLinux()
                    ? LinuxPwrite(fd, p, (nuint)bytes.Length, offset)
                    : MacPwrite(fd, p, (nuint)bytes.Length, offset);
                if (wrote != bytes.Length)
                {
                    throw new InvalidOperationException($"pwrite wrote {wrote} of {bytes.Length}");
                }
            }
        }

        public static int GetFdFlags(int fd) =>
            OperatingSystem.IsLinux() ? LinuxFcntl(fd, FGetFd) : MacFcntl(fd, FGetFd);

        public static void Close(int fd)
        {
            _ = OperatingSystem.IsLinux() ? LinuxClose(fd) : MacClose(fd);
        }

        public static int CreateSealableMemfd(int size)
        {
            const uint MfdCloexec = 1;
            const uint MfdAllowSealing = 2;
            const int FAddSeals = 1033;
            const int FSealWrite = 0x8;

            var fd = LinuxMemfd("waylandsharpest-shm-sealed", MfdCloexec | MfdAllowSealing);
            if (fd < 0)
            {
                throw new InvalidOperationException($"memfd_create failed: errno {Marshal.GetLastPInvokeError()}");
            }

            Truncate(fd, size);
            if (LinuxFcntlArg(fd, FAddSeals, FSealWrite) != 0)
            {
                throw new InvalidOperationException($"F_ADD_SEALS failed: errno {Marshal.GetLastPInvokeError()}");
            }

            return fd;
        }

        public static (int ReadEnd, int WriteEnd) Pipe()
        {
            var fds = stackalloc int[2];
            var rc = OperatingSystem.IsLinux() ? LinuxPipe(fds) : MacPipe(fds);
            if (rc != 0)
            {
                throw new InvalidOperationException($"pipe failed: errno {Marshal.GetLastPInvokeError()}");
            }

            return (fds[0], fds[1]);
        }

        [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true)]
        private static extern int LinuxMemfd([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);

        [DllImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
        private static extern int LinuxFtruncate(int fd, long length);

        [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
        private static extern int LinuxDup(int fd);

        [DllImport("libc", EntryPoint = "pwrite", SetLastError = true)]
        private static extern nint LinuxPwrite(int fd, byte* buf, nuint count, long offset);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int LinuxFcntl(int fd, int cmd);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int LinuxFcntlArg(int fd, int cmd, int arg);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int LinuxClose(int fd);

        [DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
        private static extern int LinuxPipe(int* fds);

        [DllImport("libSystem", EntryPoint = "mkstemp", SetLastError = true)]
        private static extern int MacMkstemp(byte* template);

        [DllImport("libSystem", EntryPoint = "unlink", SetLastError = true)]
        private static extern int MacUnlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport("libSystem", EntryPoint = "ftruncate", SetLastError = true)]
        private static extern int MacFtruncate(int fd, long length);

        [DllImport("libSystem", EntryPoint = "dup", SetLastError = true)]
        private static extern int MacDup(int fd);

        [DllImport("libSystem", EntryPoint = "pwrite", SetLastError = true)]
        private static extern nint MacPwrite(int fd, byte* buf, nuint count, long offset);

        [DllImport("libSystem", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int MacFcntl(int fd, int cmd);

        [DllImport("libSystem", EntryPoint = "close", SetLastError = true)]
        private static extern int MacClose(int fd);

        [DllImport("libSystem", EntryPoint = "pipe", SetLastError = true)]
        private static extern int MacPipe(int* fds);
    }
}
