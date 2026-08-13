using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Wayland.Server.Managed.Interop;

/// <summary>The epoll, eventfd and signalfd calls behind the Linux poll.</summary>
internal static unsafe partial class LinuxPoll
{
    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int epoll_create1(int flags);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int epoll_ctl(int epollFd, int operation, int fd, void* @event);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int epoll_wait(int epollFd, void* events, int maxEvents, int timeoutMs);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int eventfd(uint initialValue, int flags);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int signalfd(int fd, void* mask, int flags);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int pthread_sigmask(int how, void* set, void* oldSet);

    internal const int EPOLL_CLOEXEC = 0x80000;
    internal const int EPOLL_CTL_ADD = 1;
    internal const int EPOLL_CTL_DEL = 2;
    internal const int EPOLL_CTL_MOD = 3;

    internal const uint EPOLLIN = 0x001;
    internal const uint EPOLLOUT = 0x004;
    internal const uint EPOLLERR = 0x008;
    internal const uint EPOLLHUP = 0x010;

    internal const int EFD_NONBLOCK = 0x800;
    internal const int EFD_CLOEXEC = 0x80000;

    internal const int SFD_NONBLOCK = 0x800;
    internal const int SFD_CLOEXEC = 0x80000;

    internal const int SIG_BLOCK = 0;

    /// <summary>A <c>sigset_t</c> is 128 bytes on Linux, whatever the architecture.</summary>
    internal const int SigSetSize = 128;

    /// <summary>A <c>signalfd_siginfo</c> is 128 bytes, and the number leads it.</summary>
    internal const int SigInfoSize = 128;

    /// <summary>
    /// The <c>epoll_event</c> layout is packed on x86 but not on ARM, where the
    /// 64-bit payload forces four bytes of padding after the flags. Reading it
    /// byte by byte at the right offset is the only portable way.
    /// </summary>
    private static bool IsX86Family =>
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

    internal static int EventSize { get; } = IsX86Family ? 12 : 16;

    private static int DataOffset { get; } = IsX86Family ? 4 : 8;

    internal static void WriteEvent(Span<byte> buffer, int index, uint events, int fd)
    {
        var span = buffer.Slice(index * EventSize, EventSize);
        span.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(span, events);
        BinaryPrimitives.WriteInt32LittleEndian(span[DataOffset..], fd);
    }

    internal static (uint Events, int Fd) ReadEvent(ReadOnlySpan<byte> buffer, int index)
    {
        var span = buffer.Slice(index * EventSize, EventSize);
        return (
            BinaryPrimitives.ReadUInt32LittleEndian(span),
            BinaryPrimitives.ReadInt32LittleEndian(span[DataOffset..]));
    }

    /// <summary>Fills a <c>sigset_t</c> holding one signal.</summary>
    internal static void FillSigSet(Span<byte> set, int signalNumber)
    {
        set.Clear();
        var bit = signalNumber - 1;
        var word = bit / 64;
        var offset = word * 8;
        var value = 1UL << (bit % 64);
        BinaryPrimitives.WriteUInt64LittleEndian(set.Slice(offset, 8), value);
    }
}
