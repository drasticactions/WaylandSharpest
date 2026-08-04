using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Wayland.Native;

namespace Wayland;

/// <summary>Which half of libwayland a log handler applies to.</summary>
public enum WaylandLogSide
{
    /// <summary><c>libwayland-client</c>.</summary>
    Client,

    /// <summary><c>libwayland-server</c>.</summary>
    Server,
}

/// <summary>
/// Routes libwayland's own diagnostics — protocol errors it detects, "error in
/// client communication", <c>wl_abort</c> context — to a .NET handler instead of
/// <c>stderr</c>.
/// </summary>
/// <remarks>
/// <para>
/// libwayland's hook takes a <c>printf</c> format string and a <c>va_list</c>.
/// A <c>va_list</c> cannot be marshalled portably, so the message is formatted
/// by handing the argument straight back to <c>vsnprintf</c>. That works only
/// where <c>va_list</c> is an array type decaying to a pointer, which is the
/// case on x86-64 and AArch64 Linux — the whole of this library's supported
/// surface, since it resolves libwayland by soname. Anywhere else
/// <see cref="SetHandler"/> throws rather than guessing.
/// </para>
/// </remarks>
public static unsafe class WaylandLog
{
    /// <summary>
    /// Byte size of the platform's <c>va_list</c> element, for the copy that
    /// stands in for the <c>va_copy</c> macro: on both supported architectures
    /// it is a flat struct of pointers into the caller's frame, so copying its
    /// bytes is exactly what <c>va_copy</c> does.
    /// </summary>
    private static int VaListSize => RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 32;

    private static Action<string>? _clientHandler;
    private static Action<string>? _serverHandler;
    private static bool _clientInstalled;
    private static bool _serverInstalled;

    /// <summary>
    /// Whether the running architecture can format libwayland's log messages.
    /// True on x86-64 and AArch64.
    /// </summary>
    public static bool IsSupported =>
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>
    /// Routes <paramref name="side"/>'s diagnostics to <paramref name="handler"/>
    /// instead of <c>stderr</c>. Process-global and separate per library half.
    /// Pass <c>null</c> to restore the default, which writes the formatted
    /// message to <c>stderr</c> as libwayland's own handler does. The handler
    /// runs on whichever thread libwayland logged from and must not throw.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// The running architecture is neither x86-64 nor AArch64, where the
    /// <c>va_list</c> handling this needs is not defined.
    /// </exception>
    public static void SetHandler(WaylandLogSide side, Action<string>? handler)
    {
        if (handler is not null && !IsSupported)
        {
            throw new PlatformNotSupportedException(
                $"Routing libwayland's log output requires x86-64 or AArch64; this process is {RuntimeInformation.ProcessArchitecture}.");
        }

        // libwayland assigns the pointer unconditionally and calls it without a
        // null check, so clearing a handler must not hand it null -- the next
        // diagnostic would segfault. The thunk stays installed instead and
        // falls back to stderr, which is the default it replaced.
        if (side == WaylandLogSide.Client)
        {
            _clientHandler = handler;
            if (handler is not null && !_clientInstalled)
            {
                LibWaylandClient.wl_log_set_handler_client(&ClientThunk);
                _clientInstalled = true;
            }
        }
        else
        {
            _serverHandler = handler;
            if (handler is not null && !_serverInstalled)
            {
                LibWaylandServer.wl_log_set_handler_server(&ServerThunk);
                _serverInstalled = true;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ClientThunk(sbyte* format, nint args) => Deliver(_clientHandler, format, args);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ServerThunk(sbyte* format, nint args) => Deliver(_serverHandler, format, args);

    private static void Deliver(Action<string>? handler, sbyte* format, nint args)
    {
        try
        {
            var message = Format(format, args);
            if (handler is null)
            {
                Console.Error.Write(message);
            }
            else
            {
                handler(message);
            }
        }
        catch
        {
            // A throwing handler must not unwind into libwayland, and there is
            // nowhere to report it: this is the logging path.
        }
    }

    private static string Format(sbyte* format, nint args)
    {
        const int Stack = 1024;
        var buffer = stackalloc byte[Stack];

        // vsnprintf consumes the argument list, so the retry needs its own copy.
        var size = VaListSize;
        var saved = stackalloc byte[size];
        Buffer.MemoryCopy((void*)args, saved, size, size);

        var needed = LibC.vsnprintf(buffer, Stack, format, args);
        if (needed < 0)
        {
            return Marshal.PtrToStringUTF8((nint)format) ?? string.Empty;
        }

        if (needed < Stack)
        {
            return Encoding.UTF8.GetString(buffer, needed);
        }

        var heap = Marshal.AllocHGlobal(needed + 1);
        try
        {
            var written = LibC.vsnprintf((byte*)heap, (nuint)(needed + 1), format, (nint)saved);
            return written < 0 ? string.Empty : Encoding.UTF8.GetString((byte*)heap, written);
        }
        finally
        {
            Marshal.FreeHGlobal(heap);
        }
    }
}
