using System.Runtime.InteropServices;
using Wayland.Server.Managed.Interop;
using static Wayland.Server.Managed.Interop.Sockets;

namespace Wayland.Server;

/// <summary>
/// A client connected over an AF_UNIX socket, passing file descriptors as ancillary data. 
/// </summary>
public sealed unsafe class WlClientTransport : IWlClientTransport
{
    private readonly int _fd;
    private bool _readBroken;
    private bool _disposed;

    /// <summary>Takes ownership of a connected socket.</summary>
    public WlClientTransport(int fd)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Passing file descriptors over a socket needs Linux or macOS.");
        }

        _fd = fd;
        Libc.SetNonBlocking(fd);
        SuppressPipeSignal(fd);
    }

    /// <summary>Creates a connected pair, returning the end to hand to a client.</summary>
    /// <returns>The client's end. The server's end is owned by the returned transport.</returns>
    public static (WlClientTransport Server, int ClientFd) CreatePair()
    {
        var fds = stackalloc int[2];
        if (socketpair(AF_UNIX, SOCK_STREAM, 0, fds) != 0)
        {
            throw Libc.Failure("socketpair");
        }

        try
        {
            return (new WlClientTransport(fds[0]), fds[1]);
        }
        catch
        {
            Libc.close(fds[0]);
            Libc.close(fds[1]);
            throw;
        }
    }

    /// <inheritdoc/>
    public int? PollFd => _fd;

    /// <inheritdoc/>
    public bool IsReadBroken => _readBroken;

    /// <summary>Descriptors here are the kernel's, so there is no token table.</summary>
    public IFdSlotTable? FdSlots => null;

    /// <inheritdoc/>
    public void CloseFd(int fd)
    {
        if (fd >= 0)
        {
            Libc.close(fd);
        }
    }

    /// <inheritdoc/>
    public int DuplicateFd(int fd)
    {
        var copy = Libc.dup(fd);
        if (copy < 0)
        {
            throw new WaylandException($"dup of descriptor {fd} failed: errno {Marshal.GetLastPInvokeError()}.");
        }

        return copy;
    }

    /// <summary>Readiness comes from the event loop's poll, so the signal is unused.</summary>
    public void SetSignal(WlTransportSignal signal)
    {
    }

    /// <inheritdoc/>
    public WlClientCredentials GetCredentials() => GetPeerCredentials(_fd);

    /// <inheritdoc/>
    public void ShutdownRead()
    {
        if (!_readBroken)
        {
            _readBroken = true;
            shutdown(_fd, SHUT_RD);
        }
    }

    /// <inheritdoc/>
    public (int BytesRead, int FdsRead) TryReadNonBlocking(
        Memory<byte> buffer1,
        Memory<byte> buffer2,
        Memory<int> fdBuf1,
        Memory<int> fdBuf2)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readBroken || buffer1.Length == 0)
        {
            return (-1, 0);
        }

        using var pin1 = buffer1.Pin();
        using var pin2 = buffer2.Length > 0 ? buffer2.Pin() : default;

        var iov = stackalloc IoVec[2];
        iov[0].Base = (nint)pin1.Pointer;
        iov[0].Length = buffer1.Length;
        var iovCount = 1;

        if (buffer2.Length > 0)
        {
            iov[1].Base = (nint)pin2.Pointer;
            iov[1].Length = buffer2.Length;
            iovCount = 2;
        }

        var controlSize = CmsgSpace(sizeof(int) * MaxFdsPerMessage);
        var control = stackalloc byte[controlSize];
        new Span<byte>(control, controlSize).Clear();

        nint received;
        int flags;
        int controlReceived;
        do
        {
            received = ReceiveMessage(_fd, iov, iovCount, control, controlSize, out flags, out controlReceived);
        }
        while (received < 0 && Libc.Errno == Libc.EINTR);

        if (received < 0)
        {
            if (Libc.Errno == Libc.EAGAIN)
            {
                return (-1, 0);
            }

            throw Libc.Failure("recvmsg");
        }

        if (received == 0)
        {
            return (0, 0);
        }

        var controlSpan = new ReadOnlySpan<byte>(control, Math.Min(controlReceived, controlSize));

        if ((flags & MSG_CTRUNC) != 0)
        {
            CloseReceived(controlSpan);
            ShutdownRead();
            throw new WaylandException(
                $"A client sent more than {MaxFdsPerMessage} file descriptors in one message. " +
                "The ancillary data was truncated and the descriptors it named cannot be recovered.");
        }

        var capacity = fdBuf1.Length + fdBuf2.Length;
        var span1 = fdBuf1.Span;
        var span2 = fdBuf2.Span;
        var count = 0;
        var offset = 0;
        while (TryReadNextRights(controlSpan, ref offset, out var fds))
        {
            foreach (var fd in fds)
            {
                if (count >= capacity)
                {
                    CloseReceived(controlSpan);
                    ShutdownRead();
                    throw new WaylandException(
                        "A client sent more file descriptors than the connection can hold.");
                }

                if (IsMac)
                {
                    Libc.SetCloseOnExec(fd);
                }

                if (count < span1.Length)
                {
                    span1[count] = fd;
                }
                else
                {
                    span2[count - span1.Length] = fd;
                }

                count++;
            }
        }

        return ((int)received, count);
    }

    /// <inheritdoc/>
    public int TryWriteNonBlocking(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<int> fds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fds.Length > MaxFdsPerMessage)
        {
            throw new ArgumentException(
                $"A message may carry at most {MaxFdsPerMessage} file descriptors, not {fds.Length}.",
                nameof(fds));
        }

        using var pin = buffer.Pin();

        IoVec iov;
        iov.Base = (nint)pin.Pointer;
        iov.Length = buffer.Length;

        nint sent;
        if (fds.Length == 0)
        {
            do
            {
                sent = SendMessage(_fd, &iov, null, 0);
            }
            while (sent < 0 && Libc.Errno == Libc.EINTR);
        }
        else
        {
            var controlSize = CmsgSpace(sizeof(int) * fds.Length);
            var control = stackalloc byte[controlSize];
            WriteRights(control, fds.Span);

            do
            {
                sent = SendMessage(_fd, &iov, control, controlSize);
            }
            while (sent < 0 && Libc.Errno == Libc.EINTR);
        }

        if (sent < 0)
        {
            if (Libc.Errno == Libc.EAGAIN)
            {
                return -1;
            }

            throw Libc.Failure("sendmsg");
        }

        return (int)sent;
    }

    private static void CloseReceived(ReadOnlySpan<byte> control)
    {
        var offset = 0;
        while (TryReadNextRights(control, ref offset, out var fds))
        {
            foreach (var fd in fds)
            {
                Libc.close(fd);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Libc.close(_fd);
    }
}
