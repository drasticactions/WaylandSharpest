
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
        private readonly NativeSection _section;
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
                _section = region.RetainSection(size, out var actual);
                if (actual < size)
                {
                    _section.Release();
                    throw new WaylandException($"Mapping of {size} bytes exceeds the {actual}-byte region.");
                }

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
                return _region.IsLazy
                    ? new ReadOnlySpan<byte>(_region.Pointer, Math.Min(_size, _region.Committed))
                    : new ReadOnlySpan<byte>(_section.Pointer, _size);
            }
        }

        public nint Address
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _region.IsLazy ? (nint)_region.Pointer : (nint)_section.Pointer;
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
            _section.Release();
            _region.Release();
        }
    }
}

/// <summary>
/// A shared-memory region backed by a native memory section, addressable
/// through <see cref="IFdSlotTable"/> slots. A view retains the section it
/// was taken from, so a grown region's old section lives until the last view
/// of it is disposed, and no file mapping is involved, which is what lets a
/// region exist on a host with no mmap at all.
/// </summary>
/// <remarks>
/// In the browser the region is lazy: a client's pool is often a reservation
/// far larger than what it touches, wasm32 has four gigabytes of address
/// space in all, and nothing there is sparse. The section then starts small
/// and grows to the high-water mark <see cref="Touch"/> names, moving as it
/// grows, so a lazy region's address is read through the region each time
/// rather than held, and <see cref="Span"/> covers only what is committed.
/// </remarks>
public sealed class SharedMemoryRegion : IFdSlotPayload
{
    private const int LazyInitialBytes = 64 * 1024;

    private readonly object _lock = new();
    private NativeSection _section;
    private int _size;
    private int _refCount;
    private bool _disposed;

    /// <summary>Allocates a zero-filled region of <paramref name="size"/> bytes, committed lazily where the host cannot afford a reservation.</summary>
    public SharedMemoryRegion(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        IsLazy = OperatingSystem.IsBrowser();
        _section = new NativeSection(IsLazy ? Math.Min(size, LazyInitialBytes) : size);
        _size = size;
    }

    /// <summary>Whether the backing grows on demand rather than being allocated in full.</summary>
    public bool IsLazy { get; }

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

    /// <summary>How many bytes from the start are backed by memory; the whole region unless it is lazy.</summary>
    public int Committed
    {
        get
        {
            lock (_lock)
            {
                return _section.Size;
            }
        }
    }

    internal unsafe byte* Pointer
    {
        get
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _section.Pointer;
            }
        }
    }

    /// <summary>
    /// The host's writable view of the committed part of the current backing
    /// section, for the transport that fills the region; a lazy region needs
    /// <see cref="Touch"/> before a write past what is committed.
    /// </summary>
    public unsafe Span<byte> Span
    {
        get
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return new Span<byte>(_section.Pointer, Math.Min(_size, _section.Size));
            }
        }
    }

    /// <summary>Commits the region up to <paramref name="end"/> bytes, which a lazy region backs on demand and every other region already has.</summary>
    public void Touch(int end)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (end > _size)
            {
                throw new ArgumentOutOfRangeException(nameof(end), "Beyond the region.");
            }

            if (!IsLazy || end <= _section.Size)
            {
                return;
            }

            var grown = Math.Max(end, Math.Min(_size, (int)Math.Min(int.MaxValue, (long)_section.Size * 2)));
            _section.Resize(grown);
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

            if (IsLazy)
            {
                _size = newSize;
                return;
            }

            var grown = new NativeSection(newSize);
            unsafe
            {
                Buffer.MemoryCopy(_section.Pointer, grown.Pointer, newSize, _size);
            }

            _section.Release();
            _section = grown;
            _size = newSize;
        }
    }

    internal NativeSection RetainSection(int size, out int actualSize)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            actualSize = Math.Min(size, _size);
            _section.Retain();
            return _section;
        }
    }

    public bool IsReleased
    {
        get
        {
            lock (_lock)
            {
                return _disposed;
            }
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
            _section.Release();
        }
    }
}
