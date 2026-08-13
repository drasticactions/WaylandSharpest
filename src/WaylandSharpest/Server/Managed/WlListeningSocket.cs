using Microsoft.Win32.SafeHandles;
using Wayland.Server.Managed.Interop;
using static Wayland.Server.Managed.Interop.Sockets;

namespace Wayland.Server.Managed;

/// <summary>
/// A bound socket clients connect to. The lock file beside it is what stops two
/// compositors claiming one name, and clients expect both to be there, so the
/// convention is followed exactly rather than approximated.
/// </summary>
internal sealed unsafe class WlListeningSocket : IDisposable
{
    private readonly string? _path;
    private readonly string? _lockPath;
    private readonly SafeFileHandle? _lockHandle;
    private readonly int _fd;
    private readonly bool _ownsPath;
    private bool _disposed;

    private WlListeningSocket(int fd, string? path, string? lockPath, SafeFileHandle? lockHandle, bool ownsPath)
    {
        _fd = fd;
        _path = path;
        _lockPath = lockPath;
        _lockHandle = lockHandle;
        _ownsPath = ownsPath;
    }

    internal int Fd => _fd;

    internal string? Name { get; private init; }

    /// <summary>Adopts a socket that is already listening, such as one from an init system.</summary>
    internal static WlListeningSocket Adopt(int fd)
    {
        Libc.SetNonBlocking(fd);
        return new WlListeningSocket(fd, null, null, null, ownsPath: false);
    }

    /// <summary>Binds a name under the runtime directory.</summary>
    internal static WlListeningSocket Bind(string name)
    {
        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            throw new WaylandException("XDG_RUNTIME_DIR is not set, so there is nowhere to put the socket.");
        }

        if (!Path.IsPathRooted(directory))
        {
            throw new WaylandException($"XDG_RUNTIME_DIR is '{directory}', which is not an absolute path.");
        }

        var path = Path.Combine(directory, name);
        var lockPath = path + ".lock";

        // Exclusive sharing is an exclusive flock on this platform, which is the
        // same lock libwayland takes, so the two refuse each other's names.
        SafeFileHandle lockHandle;
        try
        {
            lockHandle = File.OpenHandle(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new WaylandException(
                $"Another server already holds '{lockPath}', so the name '{name}' is in use.", ex);
        }

        try
        {
            // The lock is ours, so any socket file left behind is from a server
            // that is gone and can be cleared away.
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var fd = socket(AF_UNIX, SOCK_STREAM, 0);
            if (fd < 0)
            {
                throw Libc.Failure("socket");
            }

            try
            {
                Libc.SetCloseOnExec(fd);
                Libc.SetNonBlocking(fd);

                Span<byte> address = stackalloc byte[UnixAddressSize];
                var length = WriteUnixAddress(address, path);
                fixed (byte* addressPtr = address)
                {
                    if (bind(fd, addressPtr, length) != 0)
                    {
                        throw Libc.Failure($"bind({path})");
                    }
                }

                if (listen(fd, 128) != 0)
                {
                    throw Libc.Failure("listen");
                }

                return new WlListeningSocket(fd, path, lockPath, lockHandle, ownsPath: true) { Name = name };
            }
            catch
            {
                Libc.close(fd);
                throw;
            }
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    /// <summary>Binds the first free name in the range clients look through.</summary>
    internal static WlListeningSocket BindAuto()
    {
        WaylandException? last = null;
        for (var display = 0; display <= 32; display++)
        {
            try
            {
                return Bind($"wayland-{display}");
            }
            catch (WaylandException ex)
            {
                last = ex;
            }
        }

        throw new WaylandException(
            $"Every name from wayland-0 to wayland-32 is taken. The last attempt said: {last?.Message}");
    }

    /// <summary>Takes a waiting connection, or returns -1 when there is none.</summary>
    internal int TryAccept()
    {
        var fd = Accept(_fd);
        if (fd < 0 && Libc.Errno != Libc.EAGAIN && Libc.Errno != Libc.EINTR)
        {
            throw Libc.Failure("accept");
        }

        return fd;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Libc.close(_fd);

        if (!_ownsPath)
        {
            return;
        }

        TryDelete(_path);
        TryDelete(_lockPath);
        _lockHandle?.Dispose();
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
