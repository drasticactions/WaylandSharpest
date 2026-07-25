using System;
using System.Linq;
using System.Text;

namespace WaylandSharpest.Generator
{
    internal static class NameUtils
    {
        private static readonly string[] CSharpKeywords =
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
            "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while",
        };

        /// <summary>snake_case → PascalCase; leading digits get an underscore prefix.</summary>
        public static string Pascal(string snake)
        {
            var sb = new StringBuilder(snake.Length);
            foreach (var part in snake.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    sb.Append(part, 1, part.Length - 1);
                }
            }

            var result = sb.Length == 0 ? "_" : sb.ToString();
            return char.IsDigit(result[0]) ? "_" + result : result;
        }

        /// <summary>snake_case → camelCase parameter name, escaped if it is a C# keyword.</summary>
        public static string Param(string snake)
        {
            var pascal = Pascal(snake);
            var camel = char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
            return CSharpKeywords.Contains(camel) ? "@" + camel : camel;
        }

        public static string EscapeXmlDoc(string text) =>
            text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
