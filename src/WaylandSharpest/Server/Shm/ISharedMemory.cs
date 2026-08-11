namespace Wayland.Server.Shm;

/// <summary>
/// Maps a client-provided shared-memory fd-slot into the compositor's address space.
/// </summary>
public interface ISharedMemory
{
    /// <summary>
    /// Maps <paramref name="size"/> bytes of the region referenced by
    /// <paramref name="fd"/> read-only. Takes ownership of the fd-slot on every
    /// path, including failure.
    /// </summary>
    IMappedMemory Map(int fd, int size);

    /// <summary>
    /// Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes
    /// from client-shared memory at <paramref name="source"/> to
    /// <paramref name="destination"/>.
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
    /// new mapping (<c>wl_shm_pool.resize</c>).
    /// </summary>
    IMappedMemory Remap(int newSize);
}

/// <summary>Selects the <see cref="ISharedMemory"/> implementation for the host platform.</summary>
public static class SharedMemory
{
    /// <summary>
    /// The mmap-backed implementation for this host.
    /// </summary>
    public static ISharedMemory CreateForPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxSharedMemory();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOSSharedMemory();
        }

        throw new PlatformNotSupportedException(
            "No mmap-backed shared memory on this platform.");
    }
}
