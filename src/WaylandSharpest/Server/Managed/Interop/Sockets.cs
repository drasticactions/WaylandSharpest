using System.Runtime.InteropServices;

namespace Wayland.Server.Managed.Interop;

/// <summary>
/// The socket calls that carry the protocol, and the two kernels' disagreements
/// about how to describe a message. The structures below are not
/// interchangeable: a Linux-shaped header read on Darwin misparses ancillary
/// data rather than failing, so each host gets its own.
/// </summary>
internal static unsafe partial class Sockets
{
    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial nint recvmsg(int fd, void* message, int flags);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial nint sendmsg(int fd, void* message, int flags);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int socket(int domain, int type, int protocol);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int socketpair(int domain, int type, int protocol, int* fds);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int bind(int fd, void* address, uint addressLength);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int listen(int fd, int backlog);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int accept(int fd, void* address, uint* addressLength);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int accept4(int fd, void* address, uint* addressLength, int flags);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int shutdown(int fd, int how);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int getsockopt(int fd, int level, int option, void* value, uint* length);

    [LibraryImport(Libc.Library, SetLastError = true)]
    internal static partial int setsockopt(int fd, int level, int option, void* value, uint length);

    internal const int AF_UNIX = 1;
    internal const int SOCK_STREAM = 1;
    internal const int SHUT_RD = 0;

    /// <summary>Matches libwayland's limit, which is what clients are written against.</summary>
    internal const int MaxFdsPerMessage = 28;

    internal static bool IsMac => OperatingSystem.IsMacOS();

    internal static int SOL_SOCKET => IsMac ? 0xffff : 1;

    internal const int SCM_RIGHTS = 1;

    internal static int MSG_DONTWAIT => IsMac ? 0x80 : 0x40;

    internal static int MSG_CTRUNC => IsMac ? 0x20 : 0x08;

    /// <summary>
    /// Linux suppresses the pipe signal per message. Darwin has no such flag
    /// and suppresses it per socket instead, through <c>SO_NOSIGPIPE</c>.
    /// </summary>
    internal static int MSG_NOSIGNAL => IsMac ? 0 : 0x4000;

    /// <summary>
    /// Linux can mark received descriptors close-on-exec as they arrive. Darwin
    /// cannot, so each one is marked immediately afterwards instead.
    /// </summary>
    internal static int MSG_CMSG_CLOEXEC => IsMac ? 0 : 0x40000000;

    internal const int SO_NOSIGPIPE = 0x1022;
    internal const int SO_PEERCRED = 17;
    internal const int SOL_LOCAL = 0;
    internal const int LOCAL_PEERCRED = 1;
    internal const int LOCAL_PEERPID = 2;

    internal const int SOCK_CLOEXEC = 0x80000;
    internal const int SOCK_NONBLOCK = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoVec
    {
        internal nint Base;
        internal nint Length;
    }

    /// <summary>Linux: the lengths are pointer-sized.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxMsgHdr
    {
        internal nint Name;
        internal uint NameLength;
        internal IoVec* Iov;
        internal nint IovLength;
        internal nint Control;
        internal nint ControlLength;
        internal int Flags;
    }

    /// <summary>Darwin: the iov count and the control length are 32 bits.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MacMsgHdr
    {
        internal nint Name;
        internal uint NameLength;
        internal IoVec* Iov;
        internal int IovLength;
        internal nint Control;
        internal uint ControlLength;
        internal int Flags;
    }

    /// <summary>Header size and alignment differ, so every cmsg offset does.</summary>
    private static int CmsgHeaderSize => IsMac ? 12 : 16;

    private static int CmsgAlignment => IsMac ? 4 : 8;

    private static int CmsgAlign(int length) => (length + CmsgAlignment - 1) & ~(CmsgAlignment - 1);

    internal static int CmsgSpace(int payloadLength) => CmsgHeaderSize + CmsgAlign(payloadLength);

    private static int CmsgLen(int payloadLength) => CmsgHeaderSize + payloadLength;

    /// <summary>Receives into up to two buffers, reporting any descriptors that came with them.</summary>
    internal static nint ReceiveMessage(
        int fd,
        IoVec* iov,
        int iovCount,
        byte* control,
        int controlLength,
        out int flags,
        out int controlReceived)
    {
        nint received;
        if (IsMac)
        {
            var header = new MacMsgHdr
            {
                Iov = iov,
                IovLength = iovCount,
                Control = (nint)control,
                ControlLength = (uint)controlLength,
            };
            received = recvmsg(fd, &header, MSG_DONTWAIT);
            flags = header.Flags;
            controlReceived = (int)header.ControlLength;
        }
        else
        {
            var header = new LinuxMsgHdr
            {
                Iov = iov,
                IovLength = iovCount,
                Control = (nint)control,
                ControlLength = controlLength,
            };
            received = recvmsg(fd, &header, MSG_DONTWAIT | MSG_CMSG_CLOEXEC);
            flags = header.Flags;
            controlReceived = (int)header.ControlLength;
        }

        return received;
    }

    /// <summary>Sends one buffer, delivering any descriptors with its first byte.</summary>
    internal static nint SendMessage(int fd, IoVec* iov, byte* control, int controlLength)
    {
        if (IsMac)
        {
            var header = new MacMsgHdr
            {
                Iov = iov,
                IovLength = 1,
                Control = (nint)control,
                ControlLength = (uint)controlLength,
            };
            return sendmsg(fd, &header, MSG_DONTWAIT);
        }

        var linux = new LinuxMsgHdr
        {
            Iov = iov,
            IovLength = 1,
            Control = (nint)control,
            ControlLength = controlLength,
        };
        return sendmsg(fd, &linux, MSG_DONTWAIT | MSG_NOSIGNAL);
    }

    /// <summary>Writes a single rights header carrying <paramref name="fds"/>.</summary>
    internal static void WriteRights(byte* control, ReadOnlySpan<int> fds)
    {
        var payload = fds.Length * sizeof(int);
        var span = new Span<byte>(control, CmsgSpace(payload));
        span.Clear();

        if (IsMac)
        {
            *(uint*)control = (uint)CmsgLen(payload);
            *(int*)(control + 4) = SOL_SOCKET;
            *(int*)(control + 8) = SCM_RIGHTS;
        }
        else
        {
            *(nint*)control = CmsgLen(payload);
            *(int*)(control + 8) = SOL_SOCKET;
            *(int*)(control + 12) = SCM_RIGHTS;
        }

        MemoryMarshal.AsBytes(fds).CopyTo(span[CmsgHeaderSize..]);
    }

    /// <summary>Walks the control buffer, one header at a time.</summary>
    internal static bool TryReadNextRights(
        ReadOnlySpan<byte> control,
        ref int offset,
        out ReadOnlySpan<int> fds)
    {
        fds = default;
        var headerSize = CmsgHeaderSize;
        if (offset + headerSize > control.Length)
        {
            return false;
        }

        int length;
        int level;
        int type;
        if (IsMac)
        {
            length = (int)MemoryMarshal.Read<uint>(control[offset..]);
            level = MemoryMarshal.Read<int>(control[(offset + 4)..]);
            type = MemoryMarshal.Read<int>(control[(offset + 8)..]);
        }
        else
        {
            length = (int)MemoryMarshal.Read<nint>(control[offset..]);
            level = MemoryMarshal.Read<int>(control[(offset + 8)..]);
            type = MemoryMarshal.Read<int>(control[(offset + 12)..]);
        }

        if (length < headerSize || offset + length > control.Length)
        {
            return false;
        }

        var payload = length - headerSize;
        if (level == SOL_SOCKET && type == SCM_RIGHTS && payload > 0)
        {
            fds = MemoryMarshal.Cast<byte, int>(control.Slice(offset + headerSize, payload));
        }

        offset += CmsgHeaderSize + CmsgAlign(payload);
        return true;
    }

    /// <summary>Fills a <c>sockaddr_un</c>, whose leading fields differ between the two kernels.</summary>
    internal static uint WriteUnixAddress(Span<byte> buffer, string path)
    {
        buffer.Clear();
        var bytes = System.Text.Encoding.UTF8.GetBytes(path);
        var pathCapacity = IsMac ? 104 : 108;
        if (bytes.Length >= pathCapacity)
        {
            throw new WaylandException(
                $"The socket path '{path}' is {bytes.Length} bytes, over this host's limit of {pathCapacity - 1}.");
        }

        if (IsMac)
        {
            // Darwin leads with a length byte, then a one-byte family.
            buffer[0] = (byte)(2 + bytes.Length + 1);
            buffer[1] = AF_UNIX;
            bytes.CopyTo(buffer[2..]);
            return (uint)(2 + bytes.Length + 1);
        }

        MemoryMarshal.Write(buffer, (ushort)AF_UNIX);
        bytes.CopyTo(buffer[2..]);
        return (uint)(2 + bytes.Length + 1);
    }

    internal const int UnixAddressSize = 110;

    /// <summary>Reads the peer's identity, which each kernel reports its own way.</summary>
    internal static WlClientCredentials GetPeerCredentials(int fd)
    {
        if (IsMac)
        {
            // struct xucred: version, uid, ngroups, then up to sixteen groups.
            var xucred = stackalloc uint[20];
            var length = (uint)(sizeof(uint) * 20);
            if (getsockopt(fd, SOL_LOCAL, LOCAL_PEERCRED, xucred, &length) != 0)
            {
                throw Libc.Failure("getsockopt(LOCAL_PEERCRED)");
            }

            var uid = xucred[1];
            var groupCount = (short)xucred[2];
            var gid = groupCount > 0 ? xucred[3] : 0;

            var pid = 0;
            var pidLength = (uint)sizeof(int);
            getsockopt(fd, SOL_LOCAL, LOCAL_PEERPID, &pid, &pidLength);

            return new WlClientCredentials(pid, uid, gid);
        }

        var ucred = stackalloc int[3];
        var ucredLength = (uint)(sizeof(int) * 3);
        if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, ucred, &ucredLength) != 0)
        {
            throw Libc.Failure("getsockopt(SO_PEERCRED)");
        }

        return new WlClientCredentials(ucred[0], (uint)ucred[1], (uint)ucred[2]);
    }

    /// <summary>Stops a write to a departed peer from ending the process.</summary>
    internal static void SuppressPipeSignal(int fd)
    {
        if (!IsMac)
        {
            return;
        }

        var on = 1;
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, sizeof(int));
    }

    /// <summary>Takes a connection, marking it close-on-exec however the host allows.</summary>
    internal static int Accept(int listenFd)
    {
        if (!IsMac)
        {
            return accept4(listenFd, null, null, SOCK_CLOEXEC | SOCK_NONBLOCK);
        }

        var fd = accept(listenFd, null, null);
        if (fd >= 0)
        {
            Libc.SetCloseOnExec(fd);
            Libc.SetNonBlocking(fd);
        }

        return fd;
    }
}
