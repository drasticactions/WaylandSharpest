using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Wayland.Native;

namespace Wayland.Server.Managed;

/// <summary>
/// The outbound half of a client's connection: serializes events and drains
/// them to the transport, keeping every descriptor with the message it belongs
/// to.
/// </summary>
/// <remarks>
/// Writes obey three rules. Every descriptor of an event travels with some of
/// that event's bytes. A single write carries the descriptors of at most one
/// event, and they belong to the last message in it, which is what lets a
/// transport that has to re-associate descriptors with messages do so, and
/// keeps each write inside the kernel's per-message descriptor limit. And no
/// write carries descriptors alone, because a socket will not send them
/// without at least one byte.
/// </remarks>
internal sealed unsafe class WlWireWriter
{
    private const int MaxMessageSize = 4096;

    private readonly Action<int> _closeFd;
    private readonly Func<int, int> _duplicateFd;
    private readonly Func<nint, uint> _resolveObjectId;
    private readonly List<(int ByteEnd, int FdEnd)> _fdBoundaries = [];
    private byte[] _bytes;
    private int _bytesUsed;
    private int[] _fds;
    private int _fdsUsed;

    internal WlWireWriter(
        Action<int> closeFd,
        Func<int, int> duplicateFd,
        Func<nint, uint> resolveObjectId,
        int byteCapacity = 4096,
        int fdCapacity = 32)
    {
        _closeFd = closeFd;
        _duplicateFd = duplicateFd;
        _resolveObjectId = resolveObjectId;
        _bytes = new byte[byteCapacity];
        _fds = new int[fdCapacity];
    }

    internal int BytesUsed => _bytesUsed;

    internal int FdsUsed => _fdsUsed;

    /// <summary>
    /// Serializes one event. Pointer arguments are copied here and are not
    /// retained, so the caller may free them as soon as this returns.
    /// </summary>
    internal void WriteEvent(uint objectId, uint opcode, WlWireSignature signature, ReadOnlySpan<WlArg> args)
    {
        var savedBytes = _bytesUsed;
        var savedFds = _fdsUsed;

        try
        {
            EnsureByteCapacity(8);
            _bytesUsed += 8;

            var arguments = signature.Arguments;
            for (var i = 0; i < arguments.Length; i++)
            {
                switch (arguments[i].Code)
                {
                    case 'i':
                    case 'u':
                    case 'f':
                        WriteUInt32(args[i].U);
                        break;

                    case 'h':
                        AddFd(_duplicateFd(args[i].Fd));
                        break;

                    case 'o':
                    case 'n':
                        WriteUInt32(args[i].Ptr == 0 ? 0 : _resolveObjectId(args[i].Ptr));
                        break;

                    case 's':
                        WriteString(args[i].Ptr);
                        break;

                    case 'a':
                        WriteArray(args[i].Ptr);
                        break;

                    default:
                        throw new WaylandException(
                            $"Event '{signature.Name}' has an unsupported argument type '{arguments[i].Code}'.");
                }
            }

            var messageSize = (uint)(_bytesUsed - savedBytes);
            if (messageSize > MaxMessageSize)
            {
                throw new WaylandException(
                    $"Event '{signature.Name}' serializes to {messageSize} bytes, over the {MaxMessageSize} byte limit.");
            }

            Unsafe.WriteUnaligned(ref _bytes[savedBytes], objectId);
            Unsafe.WriteUnaligned(ref _bytes[savedBytes + 4], (messageSize << 16) | (opcode & 0xffff));

            if (_fdsUsed > savedFds)
            {
                _fdBoundaries.Add((_bytesUsed, _fdsUsed));
            }
        }
        catch
        {
            for (var i = savedFds; i < _fdsUsed; i++)
            {
                _closeFd(_fds[i]);
            }

            _bytesUsed = savedBytes;
            _fdsUsed = savedFds;
            throw;
        }
    }

    /// <summary>
    /// Drains as much as the transport will take without blocking.
    /// </summary>
    /// <returns>False when data is still queued.</returns>
    internal bool TryFlush(IWlClientTransport transport)
    {
        while (_bytesUsed > 0)
        {
            if (_fdBoundaries.Count == 0)
            {
                var sent = transport.TryWriteNonBlocking(_bytes.AsMemory(0, _bytesUsed), ReadOnlyMemory<int>.Empty);
                if (sent <= 0)
                {
                    return false;
                }

                Compact(sent, 0);
            }
            else if (!TryFlushBatches(transport))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryFlushBatches(IWlClientTransport transport)
    {
        var byteStart = 0;
        var fdStart = 0;

        for (var i = 0; i < _fdBoundaries.Count; i++)
        {
            var (byteEnd, fdEnd) = _fdBoundaries[i];
            var batchFds = fdEnd - fdStart;
            var batchBytes = byteEnd - byteStart;

            var sent = transport.TryWriteNonBlocking(
                _bytes.AsMemory(byteStart, batchBytes),
                _fds.AsMemory(fdStart, batchFds));
            if (sent <= 0)
            {
                CompactFrom(byteStart, fdStart);
                return false;
            }

            // Descriptors go with the first byte or not at all, so once any
            // byte lands this end's copies are done.
            for (var j = fdStart; j < fdStart + batchFds; j++)
            {
                _closeFd(_fds[j]);
            }

            if (sent < batchBytes)
            {
                CompactFrom(byteStart + sent, fdStart + batchFds);
                return false;
            }

            byteStart = byteEnd;
            fdStart = fdEnd;
        }

        if (byteStart < _bytesUsed)
        {
            var tailBytes = _bytesUsed - byteStart;
            var sent = transport.TryWriteNonBlocking(
                _bytes.AsMemory(byteStart, tailBytes), ReadOnlyMemory<int>.Empty);
            if (sent <= 0)
            {
                CompactFrom(byteStart, fdStart);
                return false;
            }

            if (sent < tailBytes)
            {
                CompactFrom(byteStart + sent, fdStart);
                return false;
            }
        }

        Clear();
        return true;
    }

    internal void Clear()
    {
        _bytesUsed = 0;
        _fdsUsed = 0;
        _fdBoundaries.Clear();
    }

    /// <summary>Releases every descriptor that was queued but never sent.</summary>
    internal void CloseUnsentFds()
    {
        for (var i = 0; i < _fdsUsed; i++)
        {
            _closeFd(_fds[i]);
        }

        _fdsUsed = 0;
        _fdBoundaries.Clear();
    }

    private void WriteUInt32(uint value)
    {
        EnsureByteCapacity(4);
        Unsafe.WriteUnaligned(ref _bytes[_bytesUsed], value);
        _bytesUsed += 4;
    }

    private void WriteString(nint pointer)
    {
        if (pointer == 0)
        {
            WriteUInt32(0);
            return;
        }

        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)pointer);
        var total = bytes.Length + 1;
        var padded = (total + 3) & ~3;

        WriteUInt32((uint)total);
        EnsureByteCapacity(padded);
        bytes.CopyTo(_bytes.AsSpan(_bytesUsed));
        _bytes.AsSpan(_bytesUsed + bytes.Length, padded - bytes.Length).Clear();
        _bytesUsed += padded;
    }

    private void WriteArray(nint pointer)
    {
        if (pointer == 0)
        {
            WriteUInt32(0);
            return;
        }

        var array = (wl_array*)pointer;
        var length = (int)array->size;
        var padded = (length + 3) & ~3;

        WriteUInt32((uint)length);
        EnsureByteCapacity(padded);
        if (length > 0)
        {
            new ReadOnlySpan<byte>(array->data, length).CopyTo(_bytes.AsSpan(_bytesUsed));
        }

        _bytes.AsSpan(_bytesUsed + length, padded - length).Clear();
        _bytesUsed += padded;
    }

    private void AddFd(int fd)
    {
        if (_fdsUsed == _fds.Length)
        {
            Array.Resize(ref _fds, _fds.Length * 2);
        }

        _fds[_fdsUsed++] = fd;
    }

    private void Compact(int byteOffset, int fdOffset)
    {
        var remainingBytes = _bytesUsed - byteOffset;
        if (remainingBytes > 0 && byteOffset > 0)
        {
            Buffer.BlockCopy(_bytes, byteOffset, _bytes, 0, remainingBytes);
        }

        _bytesUsed = remainingBytes;

        var remainingFds = _fdsUsed - fdOffset;
        if (remainingFds > 0 && fdOffset > 0)
        {
            Buffer.BlockCopy(_fds, fdOffset * sizeof(int), _fds, 0, remainingFds * sizeof(int));
        }

        _fdsUsed = remainingFds;
    }

    private void CompactFrom(int byteOffset, int fdOffset)
    {
        Compact(byteOffset, fdOffset);

        var removeCount = 0;
        for (var i = 0; i < _fdBoundaries.Count; i++)
        {
            if (_fdBoundaries[i].FdEnd <= fdOffset)
            {
                removeCount = i + 1;
            }
            else
            {
                break;
            }
        }

        if (removeCount > 0)
        {
            _fdBoundaries.RemoveRange(0, removeCount);
        }

        for (var i = 0; i < _fdBoundaries.Count; i++)
        {
            var (byteEnd, fdEnd) = _fdBoundaries[i];
            _fdBoundaries[i] = (byteEnd - byteOffset, fdEnd - fdOffset);
        }
    }

    private void EnsureByteCapacity(int additional)
    {
        var required = _bytesUsed + additional;
        if (required <= _bytes.Length)
        {
            return;
        }

        var length = _bytes.Length;
        while (length < required)
        {
            length *= 2;
        }

        Array.Resize(ref _bytes, length);
    }
}
