using Wayland;
using Wayland.Server;
using Wayland.Server.Shm;
using Xunit;

namespace WaylandSharpest.Tests;

/// <summary>
/// Exercises the fd-less shm path end to end on any host.
/// </summary>
public sealed class TokenSharedMemoryTests
{
    private const int PageSize = 4096;

    private static void FillPattern(SharedMemoryRegion region, byte seed)
    {
        var span = region.Span;
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = (byte)(seed + i * 31);
        }
    }

    private static void AssertPattern(ReadOnlySpan<byte> span, byte seed)
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != (byte)(seed + i * 31))
            {
                Assert.Fail($"pattern mismatch at byte {i}");
            }
        }
    }

    [Fact]
    public void Map_resolves_a_minted_slot_and_consumes_it()
    {
        var table = new FdSlotTable();
        var shm = new TokenSharedMemory(table);
        var region = new SharedMemoryRegion(PageSize);
        FillPattern(region, seed: 7);

        var slot = table.Mint(region);
        using var mapping = shm.Map(slot, PageSize);
        Assert.Equal(0, table.Count);
        Assert.Equal(PageSize, mapping.Size);
        Assert.True(mapping.IsWritable);
        AssertPattern(mapping.Span, seed: 7);
    }

    [Fact]
    public void Map_consumes_the_slot_when_the_region_is_too_small()
    {
        var table = new FdSlotTable();
        var shm = new TokenSharedMemory(table);
        var slot = table.Mint(new SharedMemoryRegion(PageSize));

        Assert.Throws<WaylandException>(() => shm.Map(slot, 2 * PageSize));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Map_consumes_the_slot_when_the_payload_is_wrong()
    {
        var table = new FdSlotTable();
        var shm = new TokenSharedMemory(table);
        var slot = table.Mint("not a region");

        Assert.Throws<WaylandException>(() => shm.Map(slot, PageSize));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Writes_through_the_view_reach_the_region()
    {
        var table = new FdSlotTable();
        var shm = new TokenSharedMemory(table);
        var region = new SharedMemoryRegion(PageSize);

        using var mapping = shm.Map(table.Mint(region), PageSize);
        unsafe
        {
            *(byte*)mapping.Address = 0xC3;
        }

        Assert.Equal(0xC3, region.Span[0]);
    }

    [Fact]
    public void Grow_and_remap_keep_the_old_generation_readable()
    {
        var table = new FdSlotTable();
        var shm = new TokenSharedMemory(table);
        var region = new SharedMemoryRegion(PageSize);
        FillPattern(region, seed: 3);

        var first = shm.Map(table.Mint(region), PageSize);
        region.Grow(2 * PageSize);
        var second = first.Remap(2 * PageSize);

        AssertPattern(first.Span, seed: 3);
        Assert.Equal(2 * PageSize, second.Size);
        AssertPattern(second.Span[..PageSize], seed: 3);

        first.Dispose();
        AssertPattern(second.Span[..PageSize], seed: 3);
        second.Dispose();

        var fresh = new SharedMemoryRegion(PageSize);
        FillPattern(fresh, seed: 9);
        var reversed = shm.Map(table.Mint(fresh), PageSize);
        fresh.Grow(2 * PageSize);
        var next = reversed.Remap(2 * PageSize);
        next.Dispose();
        AssertPattern(reversed.Span, seed: 9);
        reversed.Dispose();
    }

    [Fact]
    public void Duplicated_slots_share_the_region()
    {
        var table = new FdSlotTable();
        var shm = new TokenSharedMemory(table);
        var region = new SharedMemoryRegion(PageSize);
        FillPattern(region, seed: 11);

        var slot = table.Mint(region);
        var dup = table.Duplicate(slot);
        Assert.NotEqual(slot, dup);

        using var mapping = shm.Map(slot, PageSize);
        AssertPattern(mapping.Span, seed: 11);

        Assert.Same(region, table.Resolve<SharedMemoryRegion>(dup));
        table.Close(dup);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void The_region_frees_with_its_last_slot_or_view()
    {
        var table = new FdSlotTable();
        var shm = new TokenSharedMemory(table);
        var region = new SharedMemoryRegion(PageSize);

        var mapping = shm.Map(table.Mint(region), PageSize);
        mapping.Dispose();
        Assert.Throws<ObjectDisposedException>(() => region.Span[0]);
    }

    [Fact]
    public void Closing_a_stale_slot_is_harmless()
    {
        var table = new FdSlotTable();
        var slot = table.Mint(new SharedMemoryRegion(PageSize));
        table.Close(slot);
        table.Close(slot);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public unsafe void TryCopyRows_copies_contiguous_and_strided()
    {
        var table = new FdSlotTable();
        var shm = new TokenSharedMemory(table);
        var region = new SharedMemoryRegion(PageSize);
        FillPattern(region, seed: 5);

        using var mapping = shm.Map(table.Mint(region), PageSize);
        const int rows = 16;
        const int rowBytes = 64;
        const int sourceStride = 128;

        var strided = new byte[rows * rowBytes];
        fixed (byte* dst = strided)
        {
            Assert.True(shm.TryCopyRows((nint)dst, rowBytes, mapping.Address, sourceStride, rowBytes, rows));
        }

        for (var row = 0; row < rows; row++)
        {
            Assert.True(mapping.Span.Slice(row * sourceStride, rowBytes)
                .SequenceEqual(strided.AsSpan(row * rowBytes, rowBytes)));
        }
    }
}
