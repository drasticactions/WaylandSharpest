using System.Runtime.InteropServices;

namespace Wayland.Server.Managed.Interop;

/// <summary>
/// The libc calls the managed transport needs on every Unix. Values that differ
/// between kernels are resolved once, here, rather than at each use.
/// </summary>
internal static unsafe partial class Libc
{
    internal const string Library = "libc";

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int dup(int fd);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint read(int fd, void* buffer, nint count);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint write(int fd, void* buffer, nint count);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int fcntl(int fd, int command, int argument);

    internal const int F_GETFL = 3;
    internal const int F_SETFL = 4;
    internal const int F_GETFD = 1;
    internal const int F_SETFD = 2;
    internal const int FD_CLOEXEC = 1;

    internal const int EINTR = 4;

    /// <summary>Non-blocking is a different bit on each kernel.</summary>
    internal static int O_NONBLOCK { get; } = OperatingSystem.IsMacOS() ? 0x0004 : 0x800;

    /// <summary>A read or write that would block reports a different number on each kernel.</summary>
    internal static int EAGAIN { get; } = OperatingSystem.IsMacOS() ? 35 : 11;

    internal static int Errno => Marshal.GetLastPInvokeError();

    internal static void SetNonBlocking(int fd)
    {
        var flags = fcntl(fd, F_GETFL, 0);
        if (flags < 0)
        {
            throw Failure("fcntl(F_GETFL)");
        }

        if (fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0)
        {
            throw Failure("fcntl(F_SETFL)");
        }
    }

    internal static void SetCloseOnExec(int fd)
    {
        var flags = fcntl(fd, F_GETFD, 0);
        if (flags >= 0)
        {
            fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
        }
    }

    internal static WaylandException Failure(string what) =>
        new($"{what} failed with errno {Errno}.");
}
