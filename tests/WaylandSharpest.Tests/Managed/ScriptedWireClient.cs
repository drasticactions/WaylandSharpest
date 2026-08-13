using System.Text;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// The other end of a connection, written by hand. It speaks the wire protocol
/// directly so a managed display can be driven without libwayland, which is the
/// only way to exercise it where libwayland does not exist.
/// </summary>
internal sealed class ScriptedWireClient(FakeClientTransport transport)
{
    private int _writesConsumed;

    /// <summary>Sends one request.</summary>
    internal void Send(uint objectId, uint opcode, params byte[] body)
    {
        var message = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(message.AsSpan(0), objectId);
        BitConverter.TryWriteBytes(message.AsSpan(4), ((uint)message.Length << 16) | opcode);
        body.CopyTo(message.AsSpan(8));
        transport.Enqueue(message);
    }

    /// <summary>Sends one request along with descriptors for its fd arguments.</summary>
    internal void SendWithFds(uint objectId, uint opcode, byte[] body, params int[] fds)
    {
        var message = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(message.AsSpan(0), objectId);
        BitConverter.TryWriteBytes(message.AsSpan(4), ((uint)message.Length << 16) | opcode);
        body.CopyTo(message.AsSpan(8));
        transport.Enqueue(message, fds);
    }

    /// <summary>Frames every event written since the last call.</summary>
    internal List<WireEvent> Drain()
    {
        var stream = new List<byte>();
        for (; _writesConsumed < transport.Writes.Count; _writesConsumed++)
        {
            stream.AddRange(transport.Writes[_writesConsumed].Bytes);
        }

        var events = new List<WireEvent>();
        var bytes = stream.ToArray();
        var offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            var objectId = BitConverter.ToUInt32(bytes, offset);
            var sizeAndOpcode = BitConverter.ToUInt32(bytes, offset + 4);
            var size = (int)(sizeAndOpcode >> 16);
            var opcode = sizeAndOpcode & 0xffff;
            if (size < 8 || offset + size > bytes.Length)
            {
                break;
            }

            events.Add(new WireEvent(objectId, opcode, bytes[(offset + 8)..(offset + size)]));
            offset += size;
        }

        return events;
    }

    internal static byte[] U32(uint value) => BitConverter.GetBytes(value);

    internal static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    /// <summary>Encodes a string the way the protocol does: length, bytes, terminator, padding.</summary>
    internal static byte[] Str(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var total = utf8.Length + 1;
        var padded = (total + 3) & ~3;
        var result = new byte[4 + padded];
        BitConverter.TryWriteBytes(result.AsSpan(0), (uint)total);
        utf8.CopyTo(result.AsSpan(4));
        return result;
    }
}

/// <summary>One event as it appeared on the wire.</summary>
internal sealed record WireEvent(uint ObjectId, uint Opcode, byte[] Body)
{
    internal uint UInt32At(int offset) => BitConverter.ToUInt32(Body, offset);

    /// <summary>Reads the string starting at <paramref name="offset"/>, without its terminator.</summary>
    internal string StringAt(int offset)
    {
        var length = (int)BitConverter.ToUInt32(Body, offset);
        return Encoding.UTF8.GetString(Body, offset + 4, length - 1);
    }

    /// <summary>Where the argument after the string at <paramref name="offset"/> begins.</summary>
    internal int AfterStringAt(int offset)
    {
        var length = (int)BitConverter.ToUInt32(Body, offset);
        return offset + 4 + ((length + 3) & ~3);
    }
}
