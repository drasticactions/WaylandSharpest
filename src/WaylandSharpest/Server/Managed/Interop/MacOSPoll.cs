using System.Runtime.InteropServices;

namespace Wayland.Server.Managed.Interop;

/// <summary>The kqueue calls behind the macOS poll.</summary>
internal static unsafe partial class MacOSPoll
{
    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int kqueue();

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int kevent(
        int kq,
        KEvent* changes,
        int changeCount,
        KEvent* events,
        int eventCount,
        TimeSpec* timeout);

    [LibraryImport(Libc.Library, EntryPoint = "signal", SetLastError = true)]
    internal static partial nint SetSignalDisposition(int signalNumber, nint handler);

    /// <summary>The disposition that leaves a signal to the queue alone.</summary>
    internal static readonly nint SIG_IGN = 1;

    internal const short EVFILT_READ = -1;
    internal const short EVFILT_WRITE = -2;
    internal const short EVFILT_SIGNAL = -6;
    internal const short EVFILT_USER = -10;

    internal const ushort EV_ADD = 0x0001;
    internal const ushort EV_DELETE = 0x0002;
    internal const ushort EV_ENABLE = 0x0004;
    internal const ushort EV_DISABLE = 0x0008;
    internal const ushort EV_CLEAR = 0x0020;
    internal const ushort EV_ERROR = 0x4000;
    internal const ushort EV_EOF = 0x8000;

    internal const uint NOTE_TRIGGER = 0x01000000;

    /// <summary>The identifier the wake channel occupies; it is not a descriptor.</summary>
    internal const nuint WakeIdent = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEvent
    {
        internal nuint Ident;
        internal short Filter;
        internal ushort Flags;
        internal uint FFlags;
        internal nint Data;
        internal nint UserData;

        internal static KEvent For(nuint ident, short filter, ushort flags, uint fflags = 0) =>
            new() { Ident = ident, Filter = filter, Flags = flags, FFlags = fflags };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TimeSpec
    {
        internal nint Seconds;
        internal nint Nanoseconds;

        internal static TimeSpec FromMilliseconds(int milliseconds) => new()
        {
            Seconds = milliseconds / 1000,
            Nanoseconds = (milliseconds % 1000) * 1_000_000,
        };
    }
}
