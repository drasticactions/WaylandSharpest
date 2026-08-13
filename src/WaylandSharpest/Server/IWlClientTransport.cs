namespace Wayland.Server;

/// <summary>
/// One client's byte and fd-slot transport.
/// </summary>
public interface IWlClientTransport : IDisposable
{
    /// <summary>
    /// Reads without blocking into two byte buffers and two fd-slot buffers.
    /// </summary>
    /// <returns>
    /// The bytes and fd-slots read.
    /// </returns>
    (int BytesRead, int FdsRead) TryReadNonBlocking(
        Memory<byte> buffer1,
        Memory<byte> buffer2,
        Memory<int> fdBuf1,
        Memory<int> fdBuf2);

    /// <summary>
    /// Writes without blocking. Returns the bytes accepted, or -1 if the write
    /// would block. All <paramref name="fds"/> are delivered with the first
    /// byte or not at all.
    /// </summary>
    int TryWriteNonBlocking(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<int> fds);

    /// <summary>
    /// Stops accepting inbound data; later reads report end of stream. Writes
    /// must keep working so a final protocol error still reaches the client.
    /// </summary>
    void ShutdownRead();

    /// <summary>
    /// True once inbound data can no longer be trusted, such as after
    /// ancillary data was truncated.
    /// </summary>
    bool IsReadBroken { get; }

    /// <summary>
    /// The file descriptor to watch for readiness, or null when there is none.
    /// </summary>
    int? PollFd { get; }

    /// <summary>
    /// The token table this transport mints fd-slot values from, or null when
    /// they are kernel file descriptors.
    /// </summary>
    IFdSlotTable? FdSlots => null;

    /// <summary>
    /// Releases an fd-slot value belonging to this client.
    /// </summary>
    void CloseFd(int fd);

    /// <summary>
    /// A second fd-slot value on the same underlying object, which the caller
    /// releases separately.
    /// </summary>
    int DuplicateFd(int fd) =>
        FdSlots is { } slots
            ? slots.Duplicate(fd)
            : throw new NotSupportedException($"{GetType().Name} cannot duplicate an fd-slot.");

    /// <summary>The peer's process and user identity.</summary>
    /// <exception cref="NotSupportedException">The peer is not a local process.</exception>
    WlClientCredentials GetCredentials() =>
        throw new NotSupportedException($"{GetType().Name} has no local peer to report credentials for.");

    /// <summary>
    /// Receives the readiness signal for this client, once, as it is
    /// registered. A transport with a <see cref="PollFd"/> can ignore it.
    /// </summary>
    void SetSignal(WlTransportSignal signal);
}
