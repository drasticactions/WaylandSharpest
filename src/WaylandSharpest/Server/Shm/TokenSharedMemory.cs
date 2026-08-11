using System.IO.MemoryMappedFiles;

namespace Wayland.Server.Shm;

/// <summary>
/// <see cref="ISharedMemory"/> for fd-less transports.
/// </summary>
public sealed class TokenSharedMemory : ISharedMemory
{
    private readonly IFdSlotTable _slots;

    /// <summary>Creates the implementation over its transport's token table.</summary>
    public TokenSharedMemory(IFdSlotTable slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        _slots = slots;
    }

    /// <inheritdoc/>
    public IMappedMemory Map(int fd, int size)
    {
        SharedMemoryRegion region;
        try
        {
            region = _slots.Resolve<SharedMemoryRegion>(fd);
        }
        catch
        {
            _slots.Close(fd);
            throw;
        }

        try
        {
            return new View(region, size);
        }
        finally
        {
            // Mirrors the mmap semantics: the view holds its own reference,
            // and the caller-supplied fd-slot is consumed on every path.
            _slots.Close(fd);
        }
    }

    /// <inheritdoc/>
    public unsafe bool TryCopyRows(nint destination, int destinationStride, nint source, int sourceStride, int rowBytes, int rows)
    {
        if (rowBytes <= 0 || rows <= 0)
        {
            return true;
        }

        var dst = (byte*)destination;
        var src = (byte*)source;
        for (var i = 0; i < rows; i++)
        {
            Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
            dst += destinationStride;
            src += sourceStride;
        }

        return true;
    }

    private sealed unsafe class View : IMappedMemory
    {
        private readonly SharedMemoryRegion _region;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly byte* _pointer;
        private readonly int _size;
        private bool _disposed;

        internal View(SharedMemoryRegion region, int size)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Mapping size must be positive.");
            }

            _region = region;
            region.AddRef();
            try
            {
                _accessor = region.CreateView(size, out var actual);
                if (actual < size)
                {
                    _accessor.Dispose();
                    throw new WaylandException($"Mapping of {size} bytes exceeds the {actual}-byte region.");
                }

                byte* pointer = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                _pointer = pointer;
                _size = size;
            }
            catch
            {
                region.Release();
                throw;
            }
        }

        public ReadOnlySpan<byte> Span
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return new ReadOnlySpan<byte>(_pointer, _size);
            }
        }

        public nint Address
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return (nint)_pointer;
            }
        }

        public int Size => _size;

        public bool IsWritable => true;

        public IMappedMemory Remap(int newSize)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new View(_region, newSize);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _region.Release();
        }
    }
}

/// <summary>
/// A shared-memory region backed by a <see cref="MemoryMappedFile"/>,
/// addressable through <see cref="IFdSlotTable"/> slots.
/// </summary>
public sealed class SharedMemoryRegion : IFdSlotPayload
{
    private readonly object _lock = new();
    private MemoryMappedFile _file;
    private MemoryMappedViewAccessor _accessor;
    private int _size;
    private int _refCount;
    private bool _disposed;

    /// <summary>Allocates a zero-filled region of <paramref name="size"/> bytes.</summary>
    public SharedMemoryRegion(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        _file = MemoryMappedFile.CreateNew(mapName: null, size);
        _accessor = _file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
        _size = size;
    }

    /// <summary>The current region size in bytes.</summary>
    public int Size
    {
        get
        {
            lock (_lock)
            {
                return _size;
            }
        }
    }

    /// <summary>
    /// The host's writable view of the current backing section, for the
    /// transport that fills the region.
    /// </summary>
    public unsafe Span<byte> Span
    {
        get
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                byte* pointer = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                return new Span<byte>(pointer, _size);
            }
        }
    }

    /// <summary>
    /// Grows the region, copying existing content into the new section. Views
    /// of the old section stay valid until disposed. Shrinking throws.
    /// </summary>
    public void Grow(int newSize)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (newSize < _size)
            {
                throw new ArgumentOutOfRangeException(nameof(newSize), "Regions never shrink.");
            }

            if (newSize == _size)
            {
                return;
            }

            var newFile = MemoryMappedFile.CreateNew(mapName: null, newSize);
            var newAccessor = newFile.CreateViewAccessor(0, newSize, MemoryMappedFileAccess.ReadWrite);
            unsafe
            {
                byte* source = null, destination = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref source);
                newAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref destination);
                try
                {
                    Buffer.MemoryCopy(source, destination, newSize, _size);
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    newAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            _accessor.Dispose();
            _file.Dispose();
            _file = newFile;
            _accessor = newAccessor;
            _size = newSize;
        }
    }

    internal MemoryMappedViewAccessor CreateView(int size, out int actualSize)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            actualSize = Math.Min(size, _size);
            return _file.CreateViewAccessor(0, actualSize, MemoryMappedFileAccess.ReadWrite);
        }
    }

    /// <inheritdoc/>
    public void AddRef() => Interlocked.Increment(ref _refCount);

    /// <inheritdoc/>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) > 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _accessor.Dispose();
            _file.Dispose();
        }
    }
}
