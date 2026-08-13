namespace Wayland;

/// <summary>
/// A message signature decoded into the per-argument form the wire codec needs.
/// Built once per <see cref="WlMessageSpec"/> and cached on it.
/// </summary>
internal sealed class WlWireSignature
{
    private WlWireSignature(string name, uint sinceVersion, WlWireArg[] arguments)
    {
        Name = name;
        SinceVersion = sinceVersion;
        Arguments = arguments;
    }

    /// <summary>The message name, for diagnostics.</summary>
    internal string Name { get; }

    /// <summary>The interface version this message was introduced in.</summary>
    internal uint SinceVersion { get; }

    internal WlWireArg[] Arguments { get; }

    internal static WlWireSignature Parse(WlMessageSpec message)
    {
        var signature = message.Signature;
        var index = 0;
        uint since = 0;
        var sawDigit = false;
        while (index < signature.Length && char.IsAsciiDigit(signature[index]))
        {
            since = (since * 10) + (uint)(signature[index] - '0');
            sawDigit = true;
            index++;
        }

        var types = message.Types;
        var arguments = new WlWireArg[types.Length];
        var argIndex = 0;
        var nullable = false;
        for (; index < signature.Length; index++)
        {
            var code = signature[index];
            if (code == '?')
            {
                nullable = true;
                continue;
            }

            if (argIndex >= arguments.Length)
            {
                throw new WaylandException(
                    $"Message '{message.Name}' has signature '{signature}' but only {types.Length} argument types.");
            }

            arguments[argIndex] = new WlWireArg(code, nullable, types[argIndex]?.Invoke());
            nullable = false;
            argIndex++;
        }

        if (argIndex != arguments.Length)
        {
            throw new WaylandException(
                $"Message '{message.Name}' has signature '{signature}' but {types.Length} argument types.");
        }

        return new WlWireSignature(message.Name, sawDigit ? since : 1, arguments);
    }
}

/// <summary>One wire argument of a <see cref="WlWireSignature"/>.</summary>
internal readonly struct WlWireArg
{
    internal WlWireArg(char code, bool isNullable, WlInterfaceSpec? iface)
    {
        Code = code;
        IsNullable = isNullable;
        Interface = iface;
    }

    /// <summary>The signature letter: one of <c>iufsonah</c>.</summary>
    internal char Code { get; }

    /// <summary>Whether the protocol allows a null object or string here.</summary>
    internal bool IsNullable { get; }

    /// <summary>The interface of an <c>o</c> or <c>n</c> argument, when the protocol names one.</summary>
    internal WlInterfaceSpec? Interface { get; }
}
