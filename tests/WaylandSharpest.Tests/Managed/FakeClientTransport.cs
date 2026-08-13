using System.Runtime.InteropServices;
using Wayland.Server;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// A transport backed by lists rather than a socket, so the codec can be driven
/// byte by byte. Write acceptance is scriptable, which is how partial writes and
/// would-block are reproduced without a full send buffer.
/// </summary>
internal sealed class FakeClientTransport : IWlClientTransport
{
    private readonly List<byte> _inboundBytes = [];
    private readonly List<int> _inboundFds = [];

    /// <summary>Every write the codec performed, in order.</summary>
    internal List<(byte[] Bytes, int[] Fds)> Writes { get; } = [];

    /// <summary>
    /// How many bytes each successive write may take. A negative entry makes
    /// the write report that it would block. Once empty, writes take everything.
    /// </summary>
    internal Queue<int> WriteLimits { get; } = new();

    /// <summary>fd-slot values released through this transport.</summary>
    internal List<int> ClosedFds { get; } = [];

    internal bool EndOfStream { get; set; }

    internal WlTransportSignal? Signal { get; private set; }

    internal int Disposals { get; private set; }

    public bool IsReadBroken { get; private set; }

    public int? PollFd => null;

    public IFdSlotTable? FdSlots { get; init; }

    /// <summary>
    /// Adds bytes and fd-slots for the codec to read. They join one stream, the
    /// way a socket's would, rather than staying separate reads.
    /// </summary>
    internal void Enqueue(byte[] bytes, params int[] fds)
    {
        _inboundBytes.AddRange(bytes);
        _inboundFds.AddRange(fds);
        Signal?.NotifyReadable();
    }

    public (int BytesRead, int FdsRead) TryReadNonBlocking(
        Memory<byte> buffer1,
        Memory<byte> buffer2,
        Memory<int> fdBuf1,
        Memory<int> fdBuf2)
    {
        if (_inboundBytes.Count == 0 && _inboundFds.Count == 0)
        {
            return EndOfStream ? (0, 0) : (-1, 0);
        }

        var bytes = CollectionsMarshal.AsSpan(_inboundBytes);
        var written = CopyInto(bytes, buffer1.Span);
        written += CopyInto(bytes[written..], buffer2.Span);
        _inboundBytes.RemoveRange(0, written);

        var fds = CollectionsMarshal.AsSpan(_inboundFds);
        var fdsWritten = CopyInto(fds, fdBuf1.Span);
        fdsWritten += CopyInto(fds[fdsWritten..], fdBuf2.Span);
        _inboundFds.RemoveRange(0, fdsWritten);

        // Descriptors can arrive ahead of the bytes of the message they belong
        // to. Reporting no bytes is not end of stream, so it has to read as a
        // read that would block.
        return (written == 0 ? -1 : written, fdsWritten);
    }

    private static int CopyInto<T>(ReadOnlySpan<T> source, Span<T> destination)
    {
        var count = Math.Min(source.Length, destination.Length);
        source[..count].CopyTo(destination);
        return count;
    }

    public int TryWriteNonBlocking(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<int> fds)
    {
        var limit = WriteLimits.Count > 0 ? WriteLimits.Dequeue() : buffer.Length;
        if (limit < 0)
        {
            return -1;
        }

        var take = Math.Min(limit, buffer.Length);
        Writes.Add((buffer.Span[..take].ToArray(), fds.Span.ToArray()));
        return take;
    }

    public void ShutdownRead() => IsReadBroken = true;

    public void CloseFd(int fd) => ClosedFds.Add(fd);

    public void SetSignal(WlTransportSignal signal)
    {
        Signal = signal;
        if (_inboundBytes.Count > 0 || _inboundFds.Count > 0)
        {
            signal.NotifyReadable();
        }
    }

    public void Dispose() => Disposals++;
}
