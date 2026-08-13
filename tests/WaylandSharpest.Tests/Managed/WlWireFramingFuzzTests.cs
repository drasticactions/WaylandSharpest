using Wayland;
using Wayland.Server.Managed;
using Xunit;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// A hostile client controls every byte and every split between reads. Whatever
/// it sends, the reader must either dispatch something well-formed or report a
/// protocol violation, so that the recovery is always to disconnect one client.
/// Anything else escaping the reader would take down the compositor.
/// </summary>
public sealed class WlWireFramingFuzzTests
{
    private static readonly WlInterfaceSpec Peer = new("fuzz_peer", 1, [], []);

    private static readonly WlInterfaceSpec Target = new(
        "fuzz_target",
        1,
        [
            new WlMessageSpec("ints", "iu", [null, null]),
            new WlMessageSpec("text", "?s", [null]),
            new WlMessageSpec("blob", "a", [null]),
            new WlMessageSpec("peer", "?o", [() => Peer]),
            new WlMessageSpec("make", "n", [() => Peer]),
            new WlMessageSpec("take", "h", [null]),
            new WlMessageSpec("everything", "iu?sa?onh", [null, null, null, null, () => Peer, () => Peer, null]),
        ],
        []);

    /// <summary>Drives one scrambled stream to exhaustion and reports nothing unexpected escaped.</summary>
    private static void Drive(byte[] stream, int[] fds, int chunk)
    {
        var host = new FakeWireHost();
        host.AddObject(1, Target);
        host.AddObject(2, Peer);

        var transport = new FakeClientTransport();
        using var reader = new WlWireReader(host, transport);

        var fdsSent = false;
        for (var offset = 0; offset < stream.Length; offset += chunk)
        {
            var slice = stream[offset..Math.Min(offset + chunk, stream.Length)];
            transport.Enqueue(slice, fdsSent ? [] : fds);
            fdsSent = true;

            reader.Readable = true;
            reader.Fill();

            try
            {
                while (reader.TryDispatchOne())
                {
                }
            }
            catch (WlProtocolViolationException)
            {
                return;
            }
        }
    }

    private static byte[] Message(uint objectId, uint opcode, byte[] body)
    {
        var message = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(message.AsSpan(0), objectId);
        BitConverter.TryWriteBytes(message.AsSpan(4), ((uint)message.Length << 16) | opcode);
        body.CopyTo(message.AsSpan(8));
        return message;
    }

    private static byte[] WellFormedStream(Random random)
    {
        var stream = new List<byte>();
        var count = random.Next(1, 6);
        for (var i = 0; i < count; i++)
        {
            switch (random.Next(6))
            {
                case 0:
                    stream.AddRange(Message(1, 0, [.. BitConverter.GetBytes(random.Next()), .. BitConverter.GetBytes(random.Next())]));
                    break;
                case 1:
                    stream.AddRange(Message(1, 1, [.. BitConverter.GetBytes(4u), (byte)'a', (byte)'b', (byte)'c', 0]));
                    break;
                case 2:
                    stream.AddRange(Message(1, 2, [.. BitConverter.GetBytes(2u), 1, 2, 0, 0]));
                    break;
                case 3:
                    stream.AddRange(Message(1, 3, BitConverter.GetBytes(2u)));
                    break;
                case 4:
                    stream.AddRange(Message(1, 4, BitConverter.GetBytes((uint)random.Next(10, 1000))));
                    break;
                default:
                    stream.AddRange(Message(1, 5, []));
                    break;
            }
        }

        return [.. stream];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(64)]
    public void Truncating_and_splitting_a_valid_stream_never_escapes(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var stream = WellFormedStream(random);
            var cut = random.Next(1, stream.Length + 1);
            var chunk = random.Next(1, 24);
            Drive(stream[..cut], [51, 52, 53, 54, 55, 56], chunk);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(97)]
    public void Corrupting_a_valid_stream_never_escapes(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var stream = WellFormedStream(random);
            var edits = random.Next(1, 5);
            for (var i = 0; i < edits; i++)
            {
                stream[random.Next(stream.Length)] = (byte)random.Next(256);
            }

            Drive(stream, [51, 52, 53, 54, 55, 56], random.Next(1, 24));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(41)]
    public void Arbitrary_bytes_never_escape(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var stream = new byte[random.Next(1, 200)];
            random.NextBytes(stream);
            Drive(stream, [51, 52, 53, 54, 55, 56], random.Next(1, 24));
        }
    }
}
