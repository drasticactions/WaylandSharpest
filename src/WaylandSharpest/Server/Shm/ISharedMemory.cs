namespace Wayland.Server.Shm;

/// <summary>
/// Maps a client-provided shared-memory fd-slot into the compositor's address
/// space. One implementation per host platform, selected through
/// <see cref="SharedMemory.CreateForPlatform"/>; the <c>wl_shm</c> buffer path
/// is the only consumer.
/// </summary>
public interface ISharedMemory
{
    /// <summary>
    /// Maps <paramref name="size"/> bytes of the region referenced by
    /// <paramref name="fd"/>, read-write when the descriptor permits it and
    /// read-only otherwise (a sealed or read-only fd) — capture protocols
    /// render into client shm buffers, which is why libwayland maps pools
    /// read-write too. Takes ownership of the fd-slot on every path, including
    /// failure — a rejected pool must not leak a descriptor.
    /// </summary>
    IMappedMemory Map(int fd, int size);

    /// <summary>
    /// Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes
    /// from client-shared memory at <paramref name="source"/> to
    /// <paramref name="destination"/>, advancing each pointer by its stride per
    /// row, without faulting the host process. Returns false — rather than
    /// raising SIGBUS — when the source cannot be read; the destination
    /// contents are then undefined.
    /// </summary>
    bool TryCopyRows(nint destination, int destinationStride, nint source, int sourceStride, int rowBytes, int rows);
}

/// <summary>A live read-only mapping of a shared-memory region.</summary>
public interface IMappedMemory : IDisposable
{
    /// <summary>The mapped bytes.</summary>
    ReadOnlySpan<byte> Span { get; }

    /// <summary>Base address, for pointer-based reads on the render path. Valid until disposal.</summary>
    nint Address { get; }

    /// <summary>Length of the mapping in bytes.</summary>
    int Size { get; }

    /// <summary>False when the fd only permitted a read-only mapping; writes through <see cref="Address"/> would then fault.</summary>
    bool IsWritable { get; }

    /// <summary>
    /// Maps the same region again at <paramref name="newSize"/> and returns the
    /// new mapping (<c>wl_shm_pool.resize</c>). This mapping — and every
    /// address resolved from it — stays valid until disposed, so a resize never
    /// moves or unmaps an address already handed out.
    /// </summary>
    IMappedMemory Remap(int newSize);
}

/// <summary>Selects the <see cref="ISharedMemory"/> implementation for the host platform.</summary>
public static class SharedMemory
{
    /// <summary>
    /// Whether this host has an mmap-backed implementation at all. False on
    /// Windows, where a client's pool fd is never a kernel descriptor, so a
    /// compositor asks before it builds one rather than catching the throw.
    /// </summary>
    public static bool SupportsPlatformMemory => PlatformFacts.IsLinuxKernel || PlatformFacts.IsApple;

    /// <summary>
    /// The mmap-backed implementation for this host. Throws
    /// <see cref="PlatformNotSupportedException"/> where client pool fds do not
    /// exist as kernel descriptors; a token-based transport constructs its
    /// <see cref="ISharedMemory"/> against its own handle table instead.
    /// </summary>
    public static ISharedMemory CreateForPlatform()
    {
        if (PlatformFacts.IsLinuxKernel)
        {
            return new LinuxSharedMemory();
        }

        if (PlatformFacts.IsApple)
        {
            return new MacOSSharedMemory();
        }

        throw new PlatformNotSupportedException(
            "No mmap-backed shared memory on this platform; use a transport-supplied implementation.");
    }
}
