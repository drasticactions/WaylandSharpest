using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace WaylandSharpest.Generator
{
    internal enum WlArgType
    {
        Int,
        Uint,
        Fixed,
        String,
        Object,
        NewId,
        Array,
        Fd,
    }

    internal sealed class ProtocolModel
    {
        public ProtocolModel(string name, string ns, List<InterfaceModel> interfaces)
        {
            Name = name;
            Namespace = ns;
            Interfaces = interfaces;
        }

        public string Name { get; }

        /// <summary>Target C# namespace for the generated code.</summary>
        public string Namespace { get; }

        public List<InterfaceModel> Interfaces { get; }
    }

    internal sealed class InterfaceModel
    {
        public InterfaceModel(string name, int version, string? summary)
        {
            Name = name;
            Version = version;
            Summary = summary;
        }

        public string Name { get; }

        public int Version { get; }

        public string? Summary { get; }

        public List<MessageModel> Requests { get; } = new List<MessageModel>();

        public List<MessageModel> Events { get; } = new List<MessageModel>();

        public List<EnumModel> Enums { get; } = new List<EnumModel>();

        /// <summary>
        /// The request disposal routes through, if any. Deliberately restricted
        /// to argument-less requests: DisposeCore() takes no parameters, so a
        /// destructor that also constructs cannot be the disposal path even
        /// though it does destroy the object.
        /// </summary>
        public MessageModel? Destructor =>
            Requests.FirstOrDefault(r => r.IsDestructor && r.Args.Count == 0);
    }

    internal sealed class MessageModel
    {
        public MessageModel(string name, int opcode, int since, bool isDestructor, string? summary)
        {
            Name = name;
            Opcode = opcode;
            Since = since;
            IsDestructor = isDestructor;
            Summary = summary;
        }

        public string Name { get; }

        public int Opcode { get; }

        public int Since { get; }

        public bool IsDestructor { get; }

        public string? Summary { get; }

        public List<ArgModel> Args { get; } = new List<ArgModel>();

        /// <summary>
        /// libwayland wire signature: optional since digits, then one letter per
        /// wire argument ('?' prefix for nullable). An interface-less new_id
        /// expands to "sun".
        /// </summary>
        public string BuildSignature()
        {
            var sb = new System.Text.StringBuilder();
            if (Since > 1)
            {
                sb.Append(Since);
            }

            foreach (var arg in Args)
            {
                if (arg.Type == WlArgType.NewId && arg.Interface is null)
                {
                    sb.Append("sun");
                    continue;
                }

                if (arg.AllowNull)
                {
                    sb.Append('?');
                }

                sb.Append(arg.Type switch
                {
                    WlArgType.Int => 'i',
                    WlArgType.Uint => 'u',
                    WlArgType.Fixed => 'f',
                    WlArgType.String => 's',
                    WlArgType.Object => 'o',
                    WlArgType.NewId => 'n',
                    WlArgType.Array => 'a',
                    WlArgType.Fd => 'h',
                    _ => throw new InvalidOperationException(),
                });
            }

            return sb.ToString();
        }

        /// <summary>Interface names per wire argument (null for untyped slots).</summary>
        public List<string?> BuildWireTypes()
        {
            var types = new List<string?>();
            foreach (var arg in Args)
            {
                if (arg.Type == WlArgType.NewId && arg.Interface is null)
                {
                    types.Add(null);
                    types.Add(null);
                    types.Add(null);
                    continue;
                }

                types.Add(arg.Type is WlArgType.Object or WlArgType.NewId ? arg.Interface : null);
            }

            return types;
        }
    }

    internal sealed class ArgModel
    {
        public ArgModel(string name, WlArgType type, string? iface, string? enumRef, bool allowNull, string? summary)
        {
            Name = name;
            Type = type;
            Interface = iface;
            EnumRef = enumRef;
            AllowNull = allowNull;
            Summary = summary;
        }

        public string Name { get; }

        public WlArgType Type { get; }

        /// <summary>Target interface name for object/new_id args, when declared.</summary>
        public string? Interface { get; }

        /// <summary>Enum reference, either "enum_name" or "interface.enum_name".</summary>
        public string? EnumRef { get; }

        public bool AllowNull { get; }

        public string? Summary { get; }
    }

    internal sealed class EnumModel
    {
        public EnumModel(string name, bool isBitfield, string? summary)
        {
            Name = name;
            IsBitfield = isBitfield;
            Summary = summary;
        }

        public string Name { get; }

        public bool IsBitfield { get; }

        public string? Summary { get; }

        public List<EnumEntryModel> Entries { get; } = new List<EnumEntryModel>();

        public bool NeedsUnsignedBacking =>
            IsBitfield || Entries.Any(e => e.Value > int.MaxValue);
    }

    internal sealed class EnumEntryModel
    {
        public EnumEntryModel(string name, long value, string? summary)
        {
            Name = name;
            Value = value;
            Summary = summary;
        }

        public string Name { get; }

        public long Value { get; }

        public string? Summary { get; }
    }

    internal static class ProtocolParser
    {
        /// <summary>
        /// Parses wayland-protocol XML. Returns null when the document is not a
        /// Wayland protocol file (so stray XML in AdditionalFiles is ignored).
        /// Throws <see cref="ProtocolParseException"/> on malformed protocol XML.
        /// </summary>
        public static ProtocolModel? Parse(string xmlText, string ns)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xmlText);
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }

            var root = doc.Root;
            if (root is null || root.Name.LocalName != "protocol")
            {
                return null;
            }

            var protocolName = (string?)root.Attribute("name")
                ?? throw new ProtocolParseException("<protocol> element is missing the 'name' attribute.");

            var interfaces = new List<InterfaceModel>();
            foreach (var ifaceElement in root.Elements("interface"))
            {
                interfaces.Add(ParseInterface(ifaceElement));
            }

            return new ProtocolModel(protocolName, ns, interfaces);
        }

        private static InterfaceModel ParseInterface(XElement element)
        {
            var name = (string?)element.Attribute("name")
                ?? throw new ProtocolParseException("<interface> element is missing the 'name' attribute.");
            var version = (int?)element.Attribute("version") ?? 1;
            var iface = new InterfaceModel(name, version, GetSummary(element));

            var opcode = 0;
            foreach (var request in element.Elements("request"))
            {
                iface.Requests.Add(ParseMessage(request, opcode++, name));
            }

            opcode = 0;
            foreach (var evt in element.Elements("event"))
            {
                iface.Events.Add(ParseMessage(evt, opcode++, name));
            }

            foreach (var enumElement in element.Elements("enum"))
            {
                iface.Enums.Add(ParseEnum(enumElement, name));
            }

            return iface;
        }

        private static MessageModel ParseMessage(XElement element, int opcode, string interfaceName)
        {
            var name = (string?)element.Attribute("name")
                ?? throw new ProtocolParseException($"A message of interface '{interfaceName}' is missing the 'name' attribute.");
            var since = (int?)element.Attribute("since") ?? 1;
            var isDestructor = (string?)element.Attribute("type") == "destructor";
            var message = new MessageModel(name, opcode, since, isDestructor, GetSummary(element));

            foreach (var arg in element.Elements("arg"))
            {
                var argName = (string?)arg.Attribute("name")
                    ?? throw new ProtocolParseException($"An argument of '{interfaceName}.{name}' is missing the 'name' attribute.");
                var typeText = (string?)arg.Attribute("type")
                    ?? throw new ProtocolParseException($"Argument '{argName}' of '{interfaceName}.{name}' is missing the 'type' attribute.");
                var type = typeText switch
                {
                    "int" => WlArgType.Int,
                    "uint" => WlArgType.Uint,
                    "fixed" => WlArgType.Fixed,
                    "string" => WlArgType.String,
                    "object" => WlArgType.Object,
                    "new_id" => WlArgType.NewId,
                    "array" => WlArgType.Array,
                    "fd" => WlArgType.Fd,
                    _ => throw new ProtocolParseException($"Argument '{argName}' of '{interfaceName}.{name}' has unknown type '{typeText}'."),
                };

                message.Args.Add(new ArgModel(
                    argName,
                    type,
                    (string?)arg.Attribute("interface"),
                    (string?)arg.Attribute("enum"),
                    (bool?)arg.Attribute("allow-null") ?? false,
                    (string?)arg.Attribute("summary")));
            }

            return message;
        }

        private static EnumModel ParseEnum(XElement element, string interfaceName)
        {
            var name = (string?)element.Attribute("name")
                ?? throw new ProtocolParseException($"An enum of interface '{interfaceName}' is missing the 'name' attribute.");
            var model = new EnumModel(name, (bool?)element.Attribute("bitfield") ?? false, GetSummary(element));

            foreach (var entry in element.Elements("entry"))
            {
                var entryName = (string?)entry.Attribute("name")
                    ?? throw new ProtocolParseException($"An entry of enum '{interfaceName}.{name}' is missing the 'name' attribute.");
                var valueText = (string?)entry.Attribute("value")
                    ?? throw new ProtocolParseException($"Entry '{entryName}' of enum '{interfaceName}.{name}' is missing the 'value' attribute.");
                model.Entries.Add(new EnumEntryModel(entryName, ParseValue(valueText, interfaceName, name), (string?)entry.Attribute("summary")));
            }

            return model;
        }

        private static long ParseValue(string text, string interfaceName, string enumName)
        {
            try
            {
                return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt64(text.Substring(2), 16)
                    : Convert.ToInt64(text, 10);
            }
            catch (Exception)
            {
                throw new ProtocolParseException($"Enum '{interfaceName}.{enumName}' has unparsable value '{text}'.");
            }
        }

        private static string? GetSummary(XElement element) =>
            (string?)element.Element("description")?.Attribute("summary")
            ?? (string?)element.Attribute("summary");
    }

    internal sealed class ProtocolParseException : Exception
    {
        public ProtocolParseException(string message) : base(message)
        {
        }
    }
}
