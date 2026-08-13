using System.Runtime.CompilerServices;
using Wayland.Native;

namespace Wayland.Server.Managed;

/// <summary>
/// The inbound half of a client's connection: reassembles the byte stream into
/// messages, decodes their arguments, and hands them to the host.
/// </summary>
/// <remarks>
/// Decoded string and array arguments point into <see cref="_scratch"/>, which
/// holds the message only for the duration of the dispatch call. Nothing the
/// reader produces outlives that call.
/// </remarks>
internal sealed unsafe class WlWireReader : IDisposable
{
    internal const int MaxMessageSize = 4096;
    internal const int MaxFdsPerMessage = 28;
    internal const int MaxPendingFds = 28;

    private const int HeaderSize = 8;
    private const int MaxArgs = 32;

    private readonly IWlWireHost _host;
    private readonly IWlClientTransport _transport;
    private readonly byte[] _scratch = GC.AllocateUninitializedArray<byte>(MaxMessageSize, pinned: true);
    private readonly wl_array[] _arrays = GC.AllocateArray<wl_array>(MaxArgs, pinned: true);
    private readonly byte* _scratchPtr;
    private readonly wl_array* _arraysPtr;
    private bool _disposed;

    internal WlWireReader(IWlWireHost host, IWlClientTransport transport)
    {
        _host = host;
        _transport = transport;
        _scratchPtr = (byte*)Unsafe.AsPointer(ref _scratch[0]);
        _arraysPtr = (wl_array*)Unsafe.AsPointer(ref _arrays[0]);
    }

    internal WlRingBuffer<byte> Data { get; } = new(MaxMessageSize * 2);

    internal WlRingBuffer<int> Fds { get; } = new(MaxMessageSize / 4);

    /// <summary>
    /// Whether the transport may have data waiting. Set when readiness is
    /// reported, cleared when a read would block.
    /// </summary>
    internal bool Readable { get; set; } = true;

    /// <summary>Whether the peer has closed, or the connection has failed.</summary>
    internal bool IsFinished { get; private set; }

    internal bool IsDisposed => _disposed;

    /// <summary>Whether both rings have room for another read.</summary>
    private bool HasBufferRoom => Data.Available > 0 && Fds.Available >= MaxFdsPerMessage;

    /// <summary>
    /// Drains the transport into the rings until it would block, the rings are
    /// full, or the peer closes.
    /// </summary>
    internal void Fill()
    {
        while (!_disposed && Readable && HasBufferRoom)
        {
            var (data1, data2) = Data.GetWriteBuffers();
            var (fd1, fd2) = Fds.GetWriteBuffers();

            int bytes;
            int fds;
            try
            {
                (bytes, fds) = _transport.TryReadNonBlocking(data1, data2, fd1, fd2);
            }
            catch (WaylandException)
            {
                IsFinished = true;
                return;
            }

            // A queued transport can report fd-slots alongside a would-block or
            // an end of stream, and they have to reach the ring either way so
            // that parsing or teardown can release them.
            if (fds > 0)
            {
                Fds.Written(fds);
            }

            if (bytes < 0)
            {
                Readable = false;
                return;
            }

            if (bytes == 0)
            {
                IsFinished = true;
                return;
            }

            Data.Written(bytes);
        }
    }

    /// <summary>
    /// Frames and dispatches one message if a whole one is buffered.
    /// </summary>
    /// <returns>False when the buffer holds no complete message.</returns>
    /// <exception cref="WlProtocolViolationException">The client broke the protocol.</exception>
    internal bool TryDispatchOne()
    {
        if (_disposed || Data.Count < HeaderSize)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        Data.Peek(header);

        var objectId = Unsafe.ReadUnaligned<uint>(ref header[0]);
        var sizeAndOpcode = Unsafe.ReadUnaligned<uint>(ref header[4]);
        var messageSize = (int)(sizeAndOpcode >> 16);
        var opcode = sizeAndOpcode & 0xffff;

        if (messageSize < HeaderSize || messageSize % 4 != 0 || messageSize > MaxMessageSize)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidMethod,
                $"Message size {messageSize} is not a valid frame for object {objectId}.");
        }

        if (Data.Count < messageSize)
        {
            return false;
        }

        var bodySize = messageSize - HeaderSize;
        Data.Skip(HeaderSize);
        Data.Read(_scratch.AsSpan(0, bodySize));

        var signature = _host.BeginRequest(objectId, opcode);
        var arguments = signature.Arguments;
        if (arguments.Length > MaxArgs)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.Implementation,
                $"Request '{signature.Name}' has {arguments.Length} arguments, more than the {MaxArgs} supported.");
        }

        Span<WlArg> args = stackalloc WlArg[arguments.Length];
        var decoded = 0;
        try
        {
            Decode(objectId, signature, bodySize, args, ref decoded);
        }
        catch
        {
            // Only the arguments that were decoded hold anything; a descriptor
            // among them has been taken off the ring and would otherwise leak.
            for (var i = 0; i < decoded; i++)
            {
                if (arguments[i].Code == 'h')
                {
                    _host.CloseFd(args[i].Fd);
                }
            }

            throw;
        }

        _host.DispatchRequest(objectId, opcode, signature, args);
        return true;
    }

    private void Decode(uint objectId, WlWireSignature signature, int bodySize, Span<WlArg> args, ref int decoded)
    {
        var arguments = signature.Arguments;
        var offset = 0;
        var arrayCount = 0;

        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            switch (argument.Code)
            {
                case 'i':
                case 'u':
                case 'f':
                    args[i].U = ReadUInt32(objectId, signature, bodySize, ref offset);
                    break;

                case 'h':
                    args[i].Fd = DequeueFd(objectId, signature);
                    break;

                case 'n':
                    args[i].U = ReadNewId(objectId, signature, bodySize, ref offset);
                    break;

                case 'o':
                    args[i].Ptr = ReadObject(objectId, signature, argument, bodySize, ref offset);
                    break;

                case 's':
                    args[i].Ptr = ReadString(objectId, signature, argument, bodySize, ref offset);
                    break;

                case 'a':
                    args[i].Ptr = ReadArray(objectId, signature, bodySize, ref offset, ref arrayCount);
                    break;

                default:
                    throw new WlProtocolViolationException(
                        objectId,
                        WlDisplayError.Implementation,
                        $"Request '{signature.Name}' has an unsupported argument type '{argument.Code}'.");
            }

            decoded = i + 1;
        }
    }

    private uint ReadUInt32(uint objectId, WlWireSignature signature, int bodySize, ref int offset)
    {
        if (offset + 4 > bodySize)
        {
            throw Truncated(objectId, signature);
        }

        var value = Unsafe.ReadUnaligned<uint>(ref _scratch[offset]);
        offset += 4;
        return value;
    }

    private uint ReadNewId(uint objectId, WlWireSignature signature, int bodySize, ref int offset)
    {
        var newId = ReadUInt32(objectId, signature, bodySize, ref offset);
        if (newId == 0)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidObject,
                $"Request '{signature.Name}' names object id 0 as a new object.");
        }

        if (newId >= WlObjectIds.ServerIdBase)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidObject,
                $"Request '{signature.Name}' names new object id {newId}, which is reserved for the server.");
        }

        var max = _host.MaxObjectId;
        if (max != 0 && newId > max)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidObject,
                $"Request '{signature.Name}' names new object id {newId}, above the limit of {max}.");
        }

        if (_host.IsObjectIdInUse(newId))
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidObject,
                $"Request '{signature.Name}' names new object id {newId}, which is already in use.");
        }

        return newId;
    }

    private nint ReadObject(uint objectId, WlWireSignature signature, WlWireArg argument, int bodySize, ref int offset)
    {
        var id = ReadUInt32(objectId, signature, bodySize, ref offset);
        if (id == 0)
        {
            if (!argument.IsNullable)
            {
                throw new WlProtocolViolationException(
                    objectId,
                    WlDisplayError.InvalidObject,
                    $"Request '{signature.Name}' has a null object argument that the protocol requires.");
            }

            return 0;
        }

        if (!_host.TryResolveObject(id, out var handle, out var interfaceName))
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidObject,
                $"Request '{signature.Name}' names unknown object {id}.");
        }

        if (argument.Interface is { } expected && interfaceName != expected.Name)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidObject,
                $"Request '{signature.Name}' expects a '{expected.Name}' but object {id} is a '{interfaceName}'.");
        }

        return handle;
    }

    private nint ReadString(uint objectId, WlWireSignature signature, WlWireArg argument, int bodySize, ref int offset)
    {
        var length = ReadUInt32(objectId, signature, bodySize, ref offset);
        if (length == 0)
        {
            if (!argument.IsNullable)
            {
                throw new WlProtocolViolationException(
                    objectId,
                    WlDisplayError.InvalidObject,
                    $"Request '{signature.Name}' has a null string argument that the protocol requires.");
            }

            return 0;
        }

        if (length > MaxMessageSize)
        {
            throw Truncated(objectId, signature);
        }

        var padded = ((int)length + 3) & ~3;
        if (offset + padded > bodySize)
        {
            throw Truncated(objectId, signature);
        }

        var start = offset;
        offset += padded;

        if (_scratch[start + (int)length - 1] != 0)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidMethod,
                $"Request '{signature.Name}' has a string argument with no terminator.");
        }

        if (_scratch.AsSpan(start, (int)length - 1).IndexOf((byte)0) >= 0)
        {
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidMethod,
                $"Request '{signature.Name}' has a string argument containing a null byte.");
        }

        return (nint)(_scratchPtr + start);
    }

    private nint ReadArray(uint objectId, WlWireSignature signature, int bodySize, ref int offset, ref int arrayCount)
    {
        var length = ReadUInt32(objectId, signature, bodySize, ref offset);
        if (length > MaxMessageSize)
        {
            throw Truncated(objectId, signature);
        }

        var padded = ((int)length + 3) & ~3;
        if (offset + padded > bodySize)
        {
            throw Truncated(objectId, signature);
        }

        var slot = arrayCount++;
        _arraysPtr[slot].size = length;
        _arraysPtr[slot].alloc = length;
        _arraysPtr[slot].data = length == 0 ? null : _scratchPtr + offset;
        offset += padded;
        return (nint)(_arraysPtr + slot);
    }

    private int DequeueFd(uint objectId, WlWireSignature signature)
    {
        Span<int> fd = stackalloc int[1];
        if (Fds.Read(fd) == 0)
        {
            // The whole message arrived, so its descriptors should have arrived
            // with it or earlier. None did, and there is no way to wait for one
            // without stalling every later message behind it.
            throw new WlProtocolViolationException(
                objectId,
                WlDisplayError.InvalidMethod,
                $"Request '{signature.Name}' expects a file descriptor but none arrived with it.");
        }

        return fd[0];
    }

    private static WlProtocolViolationException Truncated(uint objectId, WlWireSignature signature) =>
        new(objectId,
            WlDisplayError.InvalidMethod,
            $"Request '{signature.Name}' is shorter than its arguments require.");

    /// <summary>
    /// True when descriptors have piled up without a message to consume them.
    /// Checked only once no complete message can be framed, since one valid
    /// message may legitimately carry the whole per-message allowance.
    /// </summary>
    internal bool IsFloodingFds => Fds.Count > MaxPendingFds;

    /// <summary>Releases every fd-slot still queued.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Span<int> fds = stackalloc int[MaxFdsPerMessage];
        while (Fds.Count > 0)
        {
            var read = Fds.Read(fds[..Math.Min(Fds.Count, fds.Length)]);
            for (var i = 0; i < read; i++)
            {
                _transport.CloseFd(fds[i]);
            }
        }
    }
}
