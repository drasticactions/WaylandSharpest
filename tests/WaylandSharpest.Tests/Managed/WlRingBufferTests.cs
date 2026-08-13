using Wayland.Server.Managed;
using Xunit;

namespace WaylandSharpest.Tests.Managed;

/// <summary>
/// The ring has to hand out both halves of a wrap so one scattered read can
/// fill it, and has to reset when drained so the writable region stays whole.
/// </summary>
public sealed class WlRingBufferTests
{
    [Fact]
    public void An_empty_ring_offers_one_contiguous_region()
    {
        var ring = new WlRingBuffer<byte>(16);
        var (first, second) = ring.GetWriteBuffers();

        Assert.Equal(16, first.Length);
        Assert.Equal(0, second.Length);
    }

    [Fact]
    public void A_wrapped_ring_offers_both_halves()
    {
        var ring = new WlRingBuffer<byte>(16);
        ring.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        ring.Skip(8);

        var (first, second) = ring.GetWriteBuffers();

        Assert.Equal(4, first.Length);
        Assert.Equal(8, second.Length);
        Assert.Equal(12, first.Length + second.Length);
        Assert.Equal(12, ring.Available);
    }

    [Fact]
    public void Reading_across_the_wrap_returns_the_written_order()
    {
        var ring = new WlRingBuffer<byte>(8);
        ring.Write([1, 2, 3, 4, 5, 6]);
        ring.Skip(5);
        ring.Write([7, 8, 9, 10, 11]);

        var read = new byte[6];
        Assert.Equal(6, ring.Read(read));
        Assert.Equal(new byte[] { 6, 7, 8, 9, 10, 11 }, read);
    }

    [Fact]
    public void Draining_resets_the_ends()
    {
        var ring = new WlRingBuffer<byte>(8);
        ring.Write([1, 2, 3, 4, 5]);
        ring.Skip(5);

        Assert.Equal(0, ring.Count);
        var (first, second) = ring.GetWriteBuffers();
        Assert.Equal(8, first.Length);
        Assert.Equal(0, second.Length);
    }

    [Fact]
    public void Peek_leaves_the_items_in_place()
    {
        var ring = new WlRingBuffer<byte>(8);
        ring.Write([1, 2, 3, 4]);

        var peeked = new byte[2];
        Assert.Equal(2, ring.Peek(peeked));
        Assert.Equal(new byte[] { 1, 2 }, peeked);
        Assert.Equal(4, ring.Count);
    }

    [Fact]
    public void Reading_more_than_is_buffered_returns_what_there_is()
    {
        var ring = new WlRingBuffer<int>(8);
        ring.Write([1, 2]);

        var read = new int[5];
        Assert.Equal(2, ring.Read(read));
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Writing_past_capacity_throws()
    {
        var ring = new WlRingBuffer<byte>(4);
        Assert.Throws<InvalidOperationException>(() => ring.Write([1, 2, 3, 4, 5]));
        Assert.Throws<InvalidOperationException>(() => ring.Written(5));
    }
}
