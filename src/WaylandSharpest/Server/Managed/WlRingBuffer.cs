namespace Wayland.Server.Managed;

/// <summary>
/// A fixed-capacity ring for reassembling a stream. Writes go straight into the
/// buffer's own memory through <see cref="GetWriteBuffers"/> so a scattered read
/// can fill both sides of a wrap in one call; reads copy out.
/// </summary>
/// <remarks>
/// Draining the ring resets both ends to zero, which keeps the writable region
/// contiguous for as long as possible.
/// </remarks>
internal sealed class WlRingBuffer<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _tail;
    private int _count;

    internal WlRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = new T[capacity];
    }

    /// <summary>Items available to read.</summary>
    internal int Count => _count;

    internal int Capacity => _buffer.Length;

    /// <summary>Items that can still be written.</summary>
    internal int Available => _buffer.Length - _count;

    /// <summary>Copies from the read position without consuming.</summary>
    /// <returns>The number of items copied.</returns>
    internal int Peek(Span<T> destination)
    {
        var toPeek = Math.Min(_count, destination.Length);
        if (toPeek == 0)
        {
            return 0;
        }

        var firstChunk = Math.Min(toPeek, _buffer.Length - _head);
        _buffer.AsSpan(_head, firstChunk).CopyTo(destination);

        var secondChunk = toPeek - firstChunk;
        if (secondChunk > 0)
        {
            _buffer.AsSpan(0, secondChunk).CopyTo(destination[firstChunk..]);
        }

        return toPeek;
    }

    /// <summary>Copies from the read position and consumes what it copied.</summary>
    /// <returns>The number of items read.</returns>
    internal int Read(Span<T> destination)
    {
        var read = Peek(destination);
        Skip(read);
        return read;
    }

    /// <summary>Consumes <paramref name="count"/> items without copying them.</summary>
    internal void Skip(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _head = (_head + count) % _buffer.Length;
        _count -= count;

        if (_count == 0)
        {
            _head = 0;
            _tail = 0;
        }
    }

    /// <summary>
    /// The two regions that together cover all writable space: the first runs
    /// from the write position to the end of the buffer, the second covers the
    /// wrap. Either may be empty. Commit with <see cref="Written"/>.
    /// </summary>
    internal (Memory<T> First, Memory<T> Second) GetWriteBuffers()
    {
        var available = _buffer.Length - _count;
        if (available == 0)
        {
            return (Memory<T>.Empty, Memory<T>.Empty);
        }

        var tailToEnd = _buffer.Length - _tail;
        if (tailToEnd >= available)
        {
            return (_buffer.AsMemory(_tail, available), Memory<T>.Empty);
        }

        return (_buffer.AsMemory(_tail, tailToEnd), _buffer.AsMemory(0, available - tailToEnd));
    }

    /// <summary>Commits items written into the regions from <see cref="GetWriteBuffers"/>.</summary>
    internal void Written(int count)
    {
        if (count < 0 || count > _buffer.Length - _count)
        {
            throw new InvalidOperationException("Write exceeds the available buffer space.");
        }

        _tail = (_tail + count) % _buffer.Length;
        _count += count;
    }

    /// <summary>Appends items, for callers that already hold the data.</summary>
    internal void Write(ReadOnlySpan<T> source)
    {
        if (source.Length > Available)
        {
            throw new InvalidOperationException("Write exceeds the available buffer space.");
        }

        var (first, second) = GetWriteBuffers();
        var toFirst = Math.Min(first.Length, source.Length);
        source[..toFirst].CopyTo(first.Span);
        if (toFirst < source.Length)
        {
            source[toFirst..].CopyTo(second.Span);
        }

        Written(source.Length);
    }
}
