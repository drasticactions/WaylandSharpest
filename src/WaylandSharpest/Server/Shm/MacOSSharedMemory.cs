using System.Runtime.InteropServices;

namespace Wayland.Server.Shm;

/// <summary>
/// macOS <c>wl_shm</c> backing store using <c>mmap(2)</c> 
/// and <c>mach_vm_read_overwrite</c> to copy memory from the compositor's 
/// address space to a client-mapped pool.
/// </summary>
public sealed class MacOSSharedMemory : ISharedMemory
{
    /// <inheritdoc/>
    public IMappedMemory Map(int fd, int size) => new Mapping(fd, size);

    /// <inheritdoc/>
    public bool TryCopyRows(nint destination, int destinationStride, nint source, int sourceStride, int rowBytes, int rows)
    {
        if (rowBytes <= 0 || rows <= 0)
        {
            return true;
        }

        var task = mach_task_self();

        // Contiguous regions collapse to a single kernel copy; otherwise one per row.
        if (destinationStride == rowBytes && sourceStride == rowBytes)
        {
            var want = (ulong)rowBytes * (ulong)rows;
            return mach_vm_read_overwrite(task, (ulong)source, want, (ulong)destination, out var got) == 0
                && got == want;
        }

        var dst = destination;
        var src = source;
        for (var i = 0; i < rows; i++)
        {
            if (mach_vm_read_overwrite(task, (ulong)src, (ulong)rowBytes, (ulong)dst, out var got) != 0
                || got != (ulong)rowBytes)
            {
                return false; // a source page faulted: drop the frame.
            }

            dst += destinationStride;
            src += sourceStride;
        }

        return true;
    }

    [DllImport("libSystem")]
    private static extern uint mach_task_self();

    [DllImport("libSystem")]
    private static extern int mach_vm_read_overwrite(uint targetTask, ulong address, ulong size, ulong data, out ulong outSize);

    private sealed unsafe class Mapping : IMappedMemory
    {
        private const int ProtRead = 0x1;
        private const int ProtWrite = 0x2;
        private const int MapShared = 0x1;
        private const int Eacces = 13;
        private const int Eperm = 1;
        private const ulong Fioclex = 0x20006601; // _IO('f', 1): set close-on-exec.
        private static readonly nint MapFailed = -1;

        private readonly int _fd;
        private nint _address;
        private readonly int _size;
        private bool _disposed;

        public Mapping(int fd, int size)
        {
            if (size <= 0)
            {
                close(fd);
                throw new ArgumentOutOfRangeException(nameof(size), "Mapping size must be positive.");
            }

            _address = mmap(0, (nuint)size, ProtRead | ProtWrite, MapShared, fd, 0);
            var mmapErrno = Marshal.GetLastPInvokeError();
            if (_address == MapFailed && mmapErrno is Eacces or Eperm)
            {
                _address = mmap(0, (nuint)size, ProtRead, MapShared, fd, 0);
                mmapErrno = Marshal.GetLastPInvokeError();
            }
            else
            {
                IsWritable = _address != MapFailed;
            }

            if (_address == MapFailed)
            {
                close(fd);
                throw new WaylandException($"mmap of {size} bytes failed: errno {mmapErrno}");
            }

            _fd = fd;
            _size = size;
        }

        public bool IsWritable { get; }

        public nint Address
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _address;
            }
        }

        public int Size => _size;

        public ReadOnlySpan<byte> Span
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return new ReadOnlySpan<byte>((void*)_address, _size);
            }
        }

        public IMappedMemory Remap(int newSize)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The new mapping owns its own fd so either mapping can be disposed first.
            var dupFd = dup(_fd);
            if (dupFd < 0)
            {
                throw new WaylandException($"dup failed: errno {Marshal.GetLastPInvokeError()}");
            }

            ioctl(dupFd, Fioclex);
            return new Mapping(dupFd, newSize);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_address != 0 && _address != MapFailed)
            {
                munmap(_address, (nuint)_size);
                _address = 0;
            }

            close(_fd);
        }

        [DllImport("libSystem", SetLastError = true)]
        private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

        [DllImport("libSystem", SetLastError = true)]
        private static extern int munmap(nint addr, nuint length);

        [DllImport("libSystem", SetLastError = true)]
        private static extern int dup(int fd);

        [DllImport("libSystem", SetLastError = true, EntryPoint = "ioctl")]
        private static extern int ioctl(int fd, ulong request);

        [DllImport("libSystem", SetLastError = true)]
        private static extern int close(int fd);
    }
}
