using System.Runtime.InteropServices;
using System.Text;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>Which way a logged protocol message was travelling.</summary>
public enum WlProtocolMessageDirection
{
    /// <summary>Client to server.</summary>
    Request,

    /// <summary>Server to client.</summary>
    Event,
}

/// <summary>Receives one protocol message. Must not throw.</summary>
/// <param name="message">
/// The message. Its storage belongs to the caller and is invalid once the
/// delegate returns.
/// </param>
public delegate void WlProtocolLogger(in WlProtocolMessage message);

/// <summary>
/// One logged protocol message: the structured form of
/// <c>WAYLAND_DEBUG=1</c>. Valid only for the duration of the callback — the
/// argument storage is libwayland's, so a copy taken outside the callback would
/// be a use-after-free.
/// </summary>
public readonly ref struct WlProtocolMessage
{
    private readonly unsafe wl_protocol_logger_message* _message;

    internal unsafe WlProtocolMessage(WlProtocolMessageDirection direction, wl_protocol_logger_message* message)
    {
        Direction = direction;
        _message = message;
    }

    /// <summary>Which way the message was travelling.</summary>
    public WlProtocolMessageDirection Direction { get; }

    /// <summary>Raw <c>wl_resource*</c> the message was sent on or to.</summary>
    public unsafe nint ResourceHandle => (nint)_message->resource;

    /// <summary>The protocol object id.</summary>
    public unsafe uint ResourceId => LibWaylandServer.wl_resource_get_id(_message->resource);

    /// <summary>The interface name of the object, e.g. <c>wl_surface</c>.</summary>
    public unsafe string InterfaceName =>
        Marshal.PtrToStringUTF8((nint)LibWaylandServer.wl_resource_get_class(_message->resource)) ?? string.Empty;

    /// <summary>The message name, e.g. <c>commit</c>.</summary>
    public unsafe string MessageName =>
        Marshal.PtrToStringUTF8((nint)_message->message->name) ?? string.Empty;

    /// <summary>The message's opcode within its interface.</summary>
    public unsafe int Opcode => _message->message_opcode;

    /// <summary>The arguments, to be read according to <see cref="Signature"/>.</summary>
    public unsafe ReadOnlySpan<WlArg> Arguments =>
        new((WlArg*)_message->arguments, _message->arguments_count);

    /// <summary>
    /// The libwayland signature of this message, for interpreting
    /// <see cref="Arguments"/>: optional leading since-version digits, then one
    /// letter per wire argument (<c>iufsonah</c>), each optionally prefixed with
    /// <c>?</c> for nullable.
    /// </summary>
    public unsafe string Signature =>
        Marshal.PtrToStringUTF8((nint)_message->message->signature) ?? string.Empty;

    /// <summary>Renders the message in the <c>WAYLAND_DEBUG</c> style.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Direction == WlProtocolMessageDirection.Request ? " -> " : "");
        sb.Append(InterfaceName).Append('@').Append(ResourceId).Append('.').Append(MessageName).Append('(');
        AppendArguments(sb);
        return sb.Append(')').ToString();
    }

    private void AppendArguments(StringBuilder sb)
    {
        var args = Arguments;
        var index = 0;
        var first = true;
        foreach (var code in Signature)
        {
            if (char.IsDigit(code) || code == '?')
            {
                continue;
            }

            if (index >= args.Length)
            {
                break;
            }

            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            AppendArgument(sb, code, args[index]);
            index++;
        }
    }

    private static void AppendArgument(StringBuilder sb, char code, WlArg arg)
    {
        switch (code)
        {
            case 'i':
                sb.Append(arg.I);
                break;
            case 'u':
                sb.Append(arg.U);
                break;
            case 'f':
                sb.Append(arg.F.ToDouble());
                break;
            case 's':
                var text = Marshal.PtrToStringUTF8(arg.Ptr);
                sb.Append(text is null ? "nil" : $"\"{text}\"");
                break;
            case 'o':
                sb.Append(arg.Ptr == 0 ? "nil" : $"0x{arg.Ptr:x}");
                break;
            case 'n':
                sb.Append("new id ").Append(arg.U);
                break;
            case 'a':
                sb.Append("array");
                break;
            case 'h':
                sb.Append("fd ").Append(arg.Fd);
                break;
            default:
                sb.Append('?');
                break;
        }
    }
}
