using Wayland;
using Wayland.Server.Managed;
using Xunit;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// Framing and argument decoding, and the rejections that keep a malformed
/// stream from reaching a compositor. Every rejection has to be a protocol
/// violation rather than an ordinary failure, because the recovery for one is
/// to disconnect a client and for the other is to crash.
/// </summary>
public sealed class WlWireReaderTests
{
    private static readonly WlInterfaceSpec Peer = new("test_peer", 3, [], []);

    private static readonly WlInterfaceSpec Target = new(
        "test_target",
        3,
        [
            new WlMessageSpec("ints", "iu", [null, null]),
            new WlMessageSpec("text", "s", [null]),
            new WlMessageSpec("maybe_text", "?s", [null]),
            new WlMessageSpec("blob", "a", [null]),
            new WlMessageSpec("peer", "o", [() => Peer]),
            new WlMessageSpec("maybe_peer", "?o", [() => Peer]),
            new WlMessageSpec("make", "n", [() => Peer]),
            new WlMessageSpec("take", "h", [null]),
            new WlMessageSpec("take_two", "hh", [null, null]),
            new WlMessageSpec("recent", "2u", [null]),
            new WlMessageSpec("fd_then_text", "hs", [null, null]),
        ],
        []);

    private readonly FakeWireHost _host = new();
    private readonly FakeClientTransport _transport = new();

    private WlWireReader NewReader()
    {
        _host.AddObject(1, Target);
        return new WlWireReader(_host, _transport);
    }

    private static byte[] Message(uint objectId, uint opcode, params byte[] body)
    {
        var message = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(message.AsSpan(0), objectId);
        BitConverter.TryWriteBytes(message.AsSpan(4), ((uint)message.Length << 16) | opcode);
        body.CopyTo(message.AsSpan(8));
        return message;
    }

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>A wire string: length including terminator, the bytes, then padding.</summary>
    private static byte[] Str(string text)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        var total = utf8.Length + 1;
        var padded = (total + 3) & ~3;
        var result = new byte[4 + padded];
        BitConverter.TryWriteBytes(result.AsSpan(0), (uint)total);
        utf8.CopyTo(result.AsSpan(4));
        return result;
    }

    private WlProtocolViolationException Rejects(byte[] message, params int[] fds)
    {
        var reader = NewReader();
        _transport.Enqueue(message, fds);
        reader.Fill();
        return Assert.Throws<WlProtocolViolationException>(() => reader.TryDispatchOne());
    }

    [Fact]
    public void Integer_arguments_decode_in_order()
    {
        var reader = NewReader();
        _transport.Enqueue(Message(1, 0, Concat(U32(unchecked((uint)-5)), U32(7))));
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        var request = Assert.Single(_host.Dispatched);
        Assert.Equal("ints", request.Name);
        Assert.Equal(-5, request.Args[0]);
        Assert.Equal(7u, request.Args[1]);
    }

    [Fact]
    public void A_string_decodes_without_copying_the_message()
    {
        var reader = NewReader();
        _transport.Enqueue(Message(1, 1, Str("hello")));
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        Assert.Equal("hello", Assert.Single(_host.Dispatched).Args[0]);
    }

    [Fact]
    public void An_absent_optional_string_decodes_as_null()
    {
        var reader = NewReader();
        _transport.Enqueue(Message(1, 2, U32(0)));
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        Assert.Null(Assert.Single(_host.Dispatched).Args[0]);
    }

    [Fact]
    public void An_array_decodes_to_its_bytes()
    {
        var reader = NewReader();
        _transport.Enqueue(Message(1, 3, Concat(U32(3), [9, 8, 7, 0])));
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.Single(_host.Dispatched).Args[0]);
    }

    [Fact]
    public void An_object_argument_resolves_to_its_handle()
    {
        var reader = NewReader();
        _host.AddObject(5, Peer);
        _transport.Enqueue(Message(1, 4, U32(5)));
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        var handle = Assert.IsType<nint>(Assert.Single(_host.Dispatched).Args[0]);
        Assert.Equal(5u, FakeWireHost.IdOfHandle(handle));
    }

    [Fact]
    public void An_absent_optional_object_decodes_as_zero()
    {
        var reader = NewReader();
        _transport.Enqueue(Message(1, 5, U32(0)));
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        Assert.Equal((nint)0, Assert.Single(_host.Dispatched).Args[0]);
    }

    [Fact]
    public void A_new_id_passes_through_as_its_number()
    {
        var reader = NewReader();
        _transport.Enqueue(Message(1, 6, U32(20)));
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        Assert.Equal(20u, Assert.Single(_host.Dispatched).Args[0]);
    }

    [Fact]
    public void A_descriptor_comes_off_the_ancillary_queue_not_the_body()
    {
        var reader = NewReader();
        _transport.Enqueue(Message(1, 7), 42);
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        Assert.Equal(42, Assert.Single(_host.Dispatched).Args[0]);
    }

    [Fact]
    public void Descriptors_that_arrived_early_still_pair_with_their_message()
    {
        var reader = NewReader();
        _transport.Enqueue([], 11);
        _transport.Enqueue(Message(1, 8), 12);
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        var request = Assert.Single(_host.Dispatched);
        Assert.Equal(11, request.Args[0]);
        Assert.Equal(12, request.Args[1]);
    }

    [Fact]
    public void A_message_split_across_reads_waits_for_the_rest()
    {
        var reader = NewReader();
        var message = Message(1, 0, Concat(U32(1), U32(2)));

        _transport.Enqueue(message[..6]);
        reader.Fill();
        Assert.False(reader.TryDispatchOne());

        // A read that would block clears readiness, which only the event loop
        // restores once the transport reports there is more to take.
        _transport.Enqueue(message[6..]);
        reader.Readable = true;
        reader.Fill();
        Assert.True(reader.TryDispatchOne());
        Assert.Single(_host.Dispatched);
    }

    [Fact]
    public void Several_messages_in_one_read_dispatch_in_order()
    {
        var reader = NewReader();
        _transport.Enqueue(Concat(
            Message(1, 0, Concat(U32(1), U32(2))),
            Message(1, 0, Concat(U32(3), U32(4)))));
        reader.Fill();

        Assert.True(reader.TryDispatchOne());
        Assert.True(reader.TryDispatchOne());
        Assert.False(reader.TryDispatchOne());
        Assert.Equal(2, _host.Dispatched.Count);
        Assert.Equal(3, _host.Dispatched[1].Args[0]);
    }

    [Fact]
    public void End_of_stream_finishes_the_reader()
    {
        var reader = NewReader();
        _transport.EndOfStream = true;
        reader.Fill();

        Assert.True(reader.IsFinished);
    }

    [Fact]
    public void A_frame_smaller_than_its_header_is_rejected()
    {
        var message = Message(1, 0, Concat(U32(1), U32(2)));
        BitConverter.TryWriteBytes(message.AsSpan(4), (4u << 16) | 0u);
        Assert.Contains("not a valid frame", Rejects(message).Message);
    }

    [Fact]
    public void A_misaligned_frame_is_rejected()
    {
        var message = Message(1, 0, Concat(U32(1), U32(2)));
        BitConverter.TryWriteBytes(message.AsSpan(4), (13u << 16) | 0u);
        Assert.Contains("not a valid frame", Rejects(message).Message);
    }

    [Fact]
    public void An_oversized_frame_is_rejected()
    {
        var message = Message(1, 0, Concat(U32(1), U32(2)));
        BitConverter.TryWriteBytes(message.AsSpan(4), (8192u << 16) | 0u);
        Assert.Contains("not a valid frame", Rejects(message).Message);
    }

    [Fact]
    public void A_body_too_short_for_its_arguments_is_rejected()
    {
        Assert.Contains("shorter than its arguments", Rejects(Message(1, 0, U32(1))).Message);
    }

    [Fact]
    public void A_string_running_past_the_body_is_rejected()
    {
        Assert.Contains("shorter than its arguments", Rejects(Message(1, 1, U32(64))).Message);
    }

    [Fact]
    public void A_string_without_a_terminator_is_rejected()
    {
        var body = Concat(U32(4), "abcd"u8.ToArray());
        Assert.Contains("no terminator", Rejects(Message(1, 1, body)).Message);
    }

    [Fact]
    public void A_string_with_an_embedded_null_is_rejected()
    {
        var body = Concat(U32(4), [(byte)'a', 0, (byte)'c', 0]);
        Assert.Contains("null byte", Rejects(Message(1, 1, body)).Message);
    }

    [Fact]
    public void A_required_object_that_is_null_is_rejected()
    {
        Assert.Contains("null object", Rejects(Message(1, 4, U32(0))).Message);
    }

    [Fact]
    public void An_object_that_does_not_exist_is_rejected()
    {
        Assert.Contains("unknown object", Rejects(Message(1, 4, U32(77))).Message);
    }

    [Fact]
    public void An_object_of_the_wrong_interface_is_rejected()
    {
        var reader = NewReader();
        _host.AddObject(5, Target);
        _transport.Enqueue(Message(1, 4, U32(5)));
        reader.Fill();

        var violation = Assert.Throws<WlProtocolViolationException>(() => reader.TryDispatchOne());
        Assert.Contains("expects a 'test_peer'", violation.Message);
    }

    [Fact]
    public void A_new_id_of_zero_is_rejected()
    {
        Assert.Contains("object id 0", Rejects(Message(1, 6, U32(0))).Message);
    }

    [Fact]
    public void A_new_id_in_the_server_range_is_rejected()
    {
        Assert.Contains("reserved for the server", Rejects(Message(1, 6, U32(0xff000000))).Message);
    }

    [Fact]
    public void A_new_id_that_is_already_taken_is_rejected()
    {
        var reader = NewReader();
        _host.AddObject(9, Peer);
        _transport.Enqueue(Message(1, 6, U32(9)));
        reader.Fill();

        var violation = Assert.Throws<WlProtocolViolationException>(() => reader.TryDispatchOne());
        Assert.Contains("already in use", violation.Message);
    }

    [Fact]
    public void A_new_id_past_the_limit_is_rejected()
    {
        var reader = NewReader();
        _host.MaxObjectId = 100;
        _transport.Enqueue(Message(1, 6, U32(500)));
        reader.Fill();

        var violation = Assert.Throws<WlProtocolViolationException>(() => reader.TryDispatchOne());
        Assert.Contains("above the limit", violation.Message);
    }

    [Fact]
    public void A_request_newer_than_the_object_is_rejected()
    {
        var reader = new WlWireReader(_host, _transport);
        _host.AddObject(1, Target);
        _transport.Enqueue(Message(1, 9, U32(1)));
        reader.Fill();

        var violation = Assert.Throws<WlProtocolViolationException>(() => reader.TryDispatchOne());
        Assert.Contains("needs version 2", violation.Message);
    }

    [Fact]
    public void A_request_on_an_unknown_object_is_rejected()
    {
        Assert.Contains("No object 44", Rejects(Message(44, 0, Concat(U32(1), U32(2)))).Message);
    }

    [Fact]
    public void A_request_with_no_such_opcode_is_rejected()
    {
        Assert.Contains("No request 99", Rejects(Message(1, 99, [])).Message);
    }

    [Fact]
    public void A_missing_descriptor_is_rejected_rather_than_waited_for()
    {
        Assert.Contains("none arrived with it", Rejects(Message(1, 7)).Message);
    }

    [Fact]
    public void A_descriptor_taken_before_a_later_failure_is_released()
    {
        var reader = NewReader();

        // The descriptor decodes, then the string runs past the body.
        _transport.Enqueue(Message(1, 10, U32(64)), 31);
        reader.Fill();

        Assert.Throws<WlProtocolViolationException>(() => reader.TryDispatchOne());
        Assert.Equal(new[] { 31 }, _host.ClosedFds);
    }

    [Fact]
    public void Queued_descriptors_are_released_on_teardown()
    {
        var reader = NewReader();
        _transport.Enqueue([], 61, 62);
        reader.Fill();

        reader.Dispose();

        Assert.Equal(new[] { 61, 62 }, _transport.ClosedFds);
    }

    [Fact]
    public void Descriptors_without_a_message_are_reported_as_flooding()
    {
        var reader = NewReader();
        _transport.Enqueue([], Enumerable.Range(1, 29).ToArray());
        reader.Fill();

        Assert.False(reader.TryDispatchOne());
        Assert.True(reader.IsFloodingFds);
    }
}
