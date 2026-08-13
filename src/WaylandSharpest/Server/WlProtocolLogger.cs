using System.Runtime.InteropServices;
using System.Text;
using Wayland.Native;

namespace Wayland.Server;

/// <summary>Receives one protocol message. Must not throw.</summary>
/// <param name="message">
/// The message. Its storage belongs to the caller and is invalid once the
/// delegate returns.
/// </param>
public delegate void WlProtocolLogger(in WlProtocolMessage message);

/// <summary>
/// One logged protocol message.
/// </summary>
public readonly ref struct WlProtocolMessage
{
    private readonly ReadOnlySpan<WlArg> _arguments;

    internal WlProtocolMessage(
        WlProtocolMessageDirection direction,
        nint resourceHandle,
        uint resourceId,
        string interfaceName,
        string messageName,
        string signature,
        int opcode,
        ReadOnlySpan<WlArg> arguments)
    {
        Direction = direction;
        ResourceHandle = resourceHandle;
        ResourceId = resourceId;
        InterfaceName = interfaceName;
        MessageName = messageName;
        Signature = signature;
        Opcode = opcode;
        _arguments = arguments;
    }

    internal unsafe WlProtocolMessage(WlProtocolMessageDirection direction, wl_protocol_logger_message* message)
        : this(
            direction,
            (nint)message->resource,
            LibWaylandServer.wl_resource_get_id(message->resource),
            Marshal.PtrToStringUTF8((nint)LibWaylandServer.wl_resource_get_class(message->resource)) ?? string.Empty,
            Marshal.PtrToStringUTF8((nint)message->message->name) ?? string.Empty,
            Marshal.PtrToStringUTF8((nint)message->message->signature) ?? string.Empty,
            message->message_opcode,
            new ReadOnlySpan<WlArg>((WlArg*)message->arguments, message->arguments_count))
    {
    }

    /// <summary>Which way the message was travelling.</summary>
    public WlProtocolMessageDirection Direction { get; }

    /// <summary>Transport handle of the object the message was sent on or to.</summary>
    public nint ResourceHandle { get; }

    /// <summary>The protocol object id.</summary>
    public uint ResourceId { get; }

    /// <summary>The interface name of the object, e.g. <c>wl_surface</c>.</summary>
    public string InterfaceName { get; }

    /// <summary>The message name, e.g. <c>commit</c>.</summary>
    public string MessageName { get; }

    /// <summary>The message's opcode within its interface.</summary>
    public int Opcode { get; }

    /// <summary>The arguments, to be read according to <see cref="Signature"/>.</summary>
    public ReadOnlySpan<WlArg> Arguments => _arguments;

    /// <summary>
    /// The signature of this message, for interpreting <see cref="Arguments"/>:
    /// optional leading since-version digits, then one letter per wire argument
    /// (<c>iufsonah</c>), each optionally prefixed with <c>?</c> for nullable.
    /// </summary>
    public string Signature { get; }

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
