using System.Runtime.InteropServices;
using Wayland;
using Wayland.Server.Managed;
using Xunit;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// Event serialization and the rules that keep descriptors with their message:
/// one event's worth per write, always with bytes, and never lost when a write
/// is refused or only partly accepted.
/// </summary>
public sealed class WlWireWriterTests
{
    private static readonly WlInterfaceSpec Target = new(
        "test_target",
        1,
        [],
        [
            new WlMessageSpec("plain", "uu", [null, null]),
            new WlMessageSpec("with_fd", "h", [null]),
            new WlMessageSpec("with_text", "?s", [null]),
            new WlMessageSpec("with_array", "a", [null]),
            new WlMessageSpec("with_object", "?o", [null]),
            new WlMessageSpec("two_fds", "hh", [null, null]),
        ]);

    private readonly List<int> _closed = [];
    private readonly List<int> _duplicated = [];
    private readonly Dictionary<nint, uint> _objectIds = [];

    private WlWireWriter NewWriter() =>
        new(_closed.Add, Duplicate, handle => _objectIds[handle]);

    private int Duplicate(int fd)
    {
        _duplicated.Add(fd);
        return fd;
    }

    private static WlWireSignature Signature(int opcode) => Target.Events[opcode].Wire;

    [Fact]
    public void An_event_serializes_to_its_header_and_arguments()
    {
        var writer = NewWriter();
        Span<WlArg> args = stackalloc WlArg[2];
        args[0].U = 7;
        args[1].U = 9;

        writer.WriteEvent(3, 0, Signature(0), args);

        var transport = new FakeClientTransport();
        Assert.True(writer.TryFlush(transport));

        var bytes = Assert.Single(transport.Writes).Bytes;
        Assert.Equal(16, bytes.Length);
        Assert.Equal(3u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal((16u << 16) | 0u, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal(7u, BitConverter.ToUInt32(bytes, 8));
        Assert.Equal(9u, BitConverter.ToUInt32(bytes, 12));
    }

    [Fact]
    public void An_events_descriptor_is_duplicated_so_the_sender_keeps_its_own()
    {
        var writer = NewWriter();
        Span<WlArg> args = stackalloc WlArg[1];
        args[0].Fd = 9;

        writer.WriteEvent(3, 1, Signature(1), args);

        Assert.Equal(new[] { 9 }, _duplicated);

        var transport = new FakeClientTransport();
        Assert.True(writer.TryFlush(transport));
        Assert.Equal(new[] { 9 }, _closed);
    }

    [Fact]
    public void A_string_is_length_prefixed_terminated_and_padded()
    {
        var writer = NewWriter();
        var text = Marshal.StringToCoTaskMemUTF8("hey");
        try
        {
            Span<WlArg> args = stackalloc WlArg[1];
            args[0].Ptr = text;
            writer.WriteEvent(1, 2, Signature(2), args);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }

        var transport = new FakeClientTransport();
        Assert.True(writer.TryFlush(transport));

        var bytes = Assert.Single(transport.Writes).Bytes;
        Assert.Equal(16, bytes.Length);
        Assert.Equal(4u, BitConverter.ToUInt32(bytes, 8));
        Assert.Equal("hey"u8.ToArray(), bytes[12..15]);
        Assert.Equal(0, bytes[15]);
    }

    [Fact]
    public void A_null_string_is_a_zero_length()
    {
        var writer = NewWriter();
        Span<WlArg> args = stackalloc WlArg[1];
        args[0].Ptr = 0;
        writer.WriteEvent(1, 2, Signature(2), args);

        var transport = new FakeClientTransport();
        Assert.True(writer.TryFlush(transport));

        var bytes = Assert.Single(transport.Writes).Bytes;
        Assert.Equal(12, bytes.Length);
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 8));
    }

    [Fact]
    public unsafe void An_array_is_copied_out_of_its_header()
    {
        var writer = NewWriter();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var array = WlNativeArray.Create(payload);
        try
        {
            Span<WlArg> args = stackalloc WlArg[1];
            args[0].Ptr = array.Pointer;
            writer.WriteEvent(1, 3, Signature(3), args);
        }
        finally
        {
            array.Dispose();
        }

        var transport = new FakeClientTransport();
        Assert.True(writer.TryFlush(transport));

        var bytes = Assert.Single(transport.Writes).Bytes;
        Assert.Equal(20, bytes.Length);
        Assert.Equal(5u, BitConverter.ToUInt32(bytes, 8));
        Assert.Equal(payload, bytes[12..17]);
        Assert.Equal(new byte[] { 0, 0, 0 }, bytes[17..20]);
    }

    [Fact]
    public void An_object_argument_becomes_its_id()
    {
        var writer = NewWriter();
        _objectIds[(nint)0x1234] = 42;

        Span<WlArg> args = stackalloc WlArg[1];
        args[0].Ptr = 0x1234;
        writer.WriteEvent(1, 4, Signature(4), args);
        args[0].Ptr = 0;
        writer.WriteEvent(1, 4, Signature(4), args);

        var transport = new FakeClientTransport();
        Assert.True(writer.TryFlush(transport));

        var bytes = Assert.Single(transport.Writes).Bytes;
        Assert.Equal(42u, BitConverter.ToUInt32(bytes, 8));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 20));
    }

    [Fact]
    public void Descriptors_travel_with_the_bytes_of_their_own_event()
    {
        var writer = NewWriter();

        Span<WlArg> plain = stackalloc WlArg[2];
        writer.WriteEvent(1, 0, Signature(0), plain);

        Span<WlArg> withFd = stackalloc WlArg[1];
        withFd[0].Fd = 11;
        writer.WriteEvent(1, 1, Signature(1), withFd);

        withFd[0].Fd = 12;
        writer.WriteEvent(1, 1, Signature(1), withFd);

        writer.WriteEvent(1, 0, Signature(0), plain);

        var transport = new FakeClientTransport();
        Assert.True(writer.TryFlush(transport));

        Assert.Equal(3, transport.Writes.Count);

        // A descriptor argument occupies no wire bytes, so an event carrying one
        // is a bare header. The descriptor-free event ahead of the first one
        // rides along with it, and each descriptor-bearing event ends a write.
        Assert.Equal(new[] { 11 }, transport.Writes[0].Fds);
        Assert.Equal(24, transport.Writes[0].Bytes.Length);
        Assert.Equal(new[] { 12 }, transport.Writes[1].Fds);
        Assert.Equal(8, transport.Writes[1].Bytes.Length);
        Assert.Empty(transport.Writes[2].Fds);
        Assert.Equal(16, transport.Writes[2].Bytes.Length);

        Assert.Equal(new[] { 11, 12 }, _closed);
    }

    [Fact]
    public void Both_descriptors_of_one_event_go_in_one_write()
    {
        var writer = NewWriter();
        Span<WlArg> args = stackalloc WlArg[2];
        args[0].Fd = 4;
        args[1].Fd = 5;
        writer.WriteEvent(1, 5, Signature(5), args);

        var transport = new FakeClientTransport();
        Assert.True(writer.TryFlush(transport));

        Assert.Equal(new[] { 4, 5 }, Assert.Single(transport.Writes).Fds);
    }

    [Fact]
    public void A_refused_write_keeps_everything_queued()
    {
        var writer = NewWriter();
        Span<WlArg> args = stackalloc WlArg[1];
        args[0].Fd = 9;
        writer.WriteEvent(1, 1, Signature(1), args);

        var transport = new FakeClientTransport();
        transport.WriteLimits.Enqueue(-1);

        Assert.False(writer.TryFlush(transport));
        Assert.Empty(transport.Writes);
        Assert.Empty(_closed);
        Assert.Equal(8, writer.BytesUsed);
        Assert.Equal(1, writer.FdsUsed);

        Assert.True(writer.TryFlush(transport));
        Assert.Equal(new[] { 9 }, Assert.Single(transport.Writes).Fds);
        Assert.Equal(new[] { 9 }, _closed);
    }

    [Fact]
    public void A_partial_write_resends_only_the_unsent_bytes()
    {
        var writer = NewWriter();
        Span<WlArg> args = stackalloc WlArg[1];
        args[0].Fd = 9;
        writer.WriteEvent(1, 1, Signature(1), args);

        var transport = new FakeClientTransport();
        transport.WriteLimits.Enqueue(5);

        Assert.False(writer.TryFlush(transport));
        Assert.Equal(5, transport.Writes[0].Bytes.Length);

        // The descriptor went with the accepted bytes, so it is not sent twice.
        Assert.Equal(new[] { 9 }, _closed);

        Assert.True(writer.TryFlush(transport));
        Assert.Equal(3, transport.Writes[1].Bytes.Length);
        Assert.Empty(transport.Writes[1].Fds);
        Assert.Equal(new[] { 9 }, _closed);
    }

    [Fact]
    public void Unsent_descriptors_are_released_on_teardown()
    {
        var writer = NewWriter();
        Span<WlArg> args = stackalloc WlArg[1];
        args[0].Fd = 21;
        writer.WriteEvent(1, 1, Signature(1), args);

        writer.CloseUnsentFds();

        Assert.Equal(new[] { 21 }, _closed);
        Assert.Equal(0, writer.FdsUsed);
    }

    [Fact]
    public void A_failed_event_leaves_no_trace_and_releases_its_descriptors()
    {
        var writer = NewWriter();
        Span<WlArg> good = stackalloc WlArg[2];
        good[0].U = 1;
        good[1].U = 2;
        writer.WriteEvent(1, 0, Signature(0), good);
        var before = writer.BytesUsed;

        var oversized = new WlInterfaceSpec(
            "test_target", 1, [], [new WlMessageSpec("huge", "ha", [null, null])]);
        var array = WlNativeArray.Create(new byte[5000]);
        var threw = false;
        try
        {
            Span<WlArg> args = stackalloc WlArg[2];
            args[0].Fd = 33;
            args[1].Ptr = array.Pointer;
            writer.WriteEvent(1, 0, oversized.Events[0].Wire, args);
        }
        catch (WaylandException)
        {
            threw = true;
        }
        finally
        {
            array.Dispose();
        }

        Assert.True(threw);

        Assert.Equal(before, writer.BytesUsed);
        Assert.Equal(0, writer.FdsUsed);
        Assert.Equal(new[] { 33 }, _closed);
    }
}
