using System.Runtime.InteropServices;

namespace Wayland.Server.Shm;

/// <summary>
/// Linux <c>wl_shm</c> backing: maps client fds read-only with <c>mmap</c>. A
/// resize maps the file again at the new size (<see cref="IMappedMemory.Remap"/>)
/// instead of <c>mremap(MREMAP_MAYMOVE)</c>, so an address handed to the render
/// path is never moved or unmapped underneath an in-flight read; the fd is
/// retained for the mapping's lifetime to make that second map possible.
/// </summary>
public sealed class LinuxSharedMemory : ISharedMemory
{
    /// <inheritdoc/>
    public IMappedMemory Map(int fd, int size) => new Mapping(fd, size);

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <c>process_vm_readv</c> against the own process: the kernel performs
    /// the copy and reports an unreadable source page (a client
    /// <c>ftruncate</c>d its pool file below the mapped size) as a short
    /// transfer / <c>EFAULT</c> instead of delivering SIGBUS to the copying
    /// thread — .NET has no equivalent of a <c>sigsetjmp</c> trap. Reading the
    /// own address space needs no ptrace permission.
    /// </remarks>
    public unsafe bool TryCopyRows(nint destination, int destinationStride, nint source, int sourceStride, int rowBytes, int rows)
    {
        if (rowBytes <= 0 || rows <= 0)
        {
            return true;
        }

        const int MaxBatch = 1024; // IOV_MAX
        const int Eintr = 4;
        var local = stackalloc IoVec[Math.Min(rows, MaxBatch)];
        var remote = stackalloc IoVec[Math.Min(rows, MaxBatch)];

        // Contiguous regions collapse to a single iovec pair; otherwise one pair per row.
        var contiguous = destinationStride == rowBytes && sourceStride == rowBytes;
        var remaining = rows;
        var dst = destination;
        var src = source;
        while (remaining > 0)
        {
            int batch;
            long want;
            if (contiguous)
            {
                batch = 1;
                want = (long)rowBytes * remaining;
                local[0] = new IoVec { Base = dst, Len = (nuint)want };
                remote[0] = new IoVec { Base = src, Len = (nuint)want };
                remaining = 0;
            }
            else
            {
                batch = Math.Min(remaining, MaxBatch);
                for (var i = 0; i < batch; i++)
                {
                    local[i] = new IoVec { Base = dst, Len = (nuint)rowBytes };
                    remote[i] = new IoVec { Base = src, Len = (nuint)rowBytes };
                    dst += destinationStride;
                    src += sourceStride;
                }

                want = (long)rowBytes * batch;
                remaining -= batch;
            }

            long got;
            do
            {
                got = process_vm_readv(Environment.ProcessId, local, (nuint)batch, remote, (nuint)batch, 0);
            }
            while (got < 0 && Marshal.GetLastPInvokeError() == Eintr);

            if (got != want)
            {
                return false; // a source page faulted (or partial transfer): drop the frame.
            }
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public nint Base;
        public nuint Len;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint process_vm_readv(int pid,
        IoVec* localIov, nuint localCount, IoVec* remoteIov, nuint remoteCount, nuint flags);

    private sealed unsafe class Mapping : IMappedMemory
    {
        private const int ProtRead = 0x1;
        private const int ProtWrite = 0x2;
        private const int MapShared = 0x1;
        private const int Eacces = 13;
        private const int Eperm = 1;
        private const int FdCloexec = 1030; // F_DUPFD_CLOEXEC
        private static readonly nint MapFailed = -1;

        private readonly int _fd;
        private nint _address;
        private readonly int _size;
        private bool _disposed;

        public Mapping(int fd, int size)
        {
            // We own the fd on every path — retained for Remap, closed with the
            // mapping (or here, when the map itself fails).
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
            var dup = fcntl(_fd, FdCloexec, 0);
            if (dup < 0)
            {
                throw new WaylandException($"dup failed: errno {Marshal.GetLastPInvokeError()}");
            }

            return new Mapping(dup, newSize);
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

        [DllImport("libc", SetLastError = true)]
        private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

        [DllImport("libc", SetLastError = true)]
        private static extern int munmap(nint addr, nuint length);

        [DllImport("libc", SetLastError = true)]
        private static extern int fcntl(int fd, int cmd, int arg);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);
    }
}
