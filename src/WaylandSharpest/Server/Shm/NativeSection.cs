using System.Runtime.InteropServices;

namespace Wayland.Server.Shm;

/// <summary>
/// One zero-filled native allocation with a reference count: a region holds
/// one reference, each view of it another, and the memory is freed when the
/// last is released.
/// </summary>
internal sealed unsafe class NativeSection
{
    private byte* _pointer;
    private int _refCount = 1;

    internal NativeSection(int size)
    {
        _pointer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        Size = size;
    }

    internal int Size { get; private set; }

    internal void Resize(int newSize)
    {
        if (newSize <= Size)
        {
            return;
        }

        var grown = (byte*)NativeMemory.Realloc(Pointer, (nuint)newSize);
        new Span<byte>(grown + Size, newSize - Size).Clear();
        _pointer = grown;
        Size = newSize;
    }

    internal byte* Pointer
    {
        get
        {
            if (_pointer is null)
            {
                throw new ObjectDisposedException(nameof(NativeSection));
            }

            return _pointer;
        }
    }

    internal void Retain() => Interlocked.Increment(ref _refCount);

    internal void Release()
    {
        if (Interlocked.Decrement(ref _refCount) > 0)
        {
            return;
        }

        var pointer = _pointer;
        _pointer = null;
        if (pointer is not null)
        {
            NativeMemory.Free(pointer);
        }
    }
}
