using System;
using System.Text;

namespace WaylandSharpest.Generator
{
    internal sealed class CodeWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;

        public void Line() => _sb.AppendLine();

        public void Line(string text)
        {
            if (text.Length == 0)
            {
                _sb.AppendLine();
                return;
            }

            _sb.Append(' ', _indent * 4);
            _sb.AppendLine(text);
        }

        public void Open(string text)
        {
            Line(text);
            Line("{");
            _indent++;
        }

        public void Close(string suffix = "")
        {
            _indent--;
            Line("}" + suffix);
        }

        public void Indent() => _indent++;

        public void Outdent() => _indent--;

        public void Doc(string? summary)
        {
            if (!string.IsNullOrWhiteSpace(summary))
            {
                Line("/// <summary>" + NameUtils.EscapeXmlDoc(summary!.Trim()) + "</summary>");
            }
        }

        /// <summary>
        /// Emits a summary followed by the protocol's normative prose as
        /// remarks. The prose keeps its own line breaks — it is hard-wrapped in
        /// the XML and reads badly re-flowed — and every line is escaped, since
        /// an unescaped <c>&lt;</c> or <c>&amp;</c> breaks the doc comment.
        /// </summary>
        public void Doc(string? summary, string? remarks)
        {
            Doc(summary);
            if (string.IsNullOrWhiteSpace(remarks))
            {
                return;
            }

            Line("/// <remarks>");
            foreach (var line in remarks!.Replace("\r\n", "\n").Split('\n'))
            {
                Line(line.Length == 0
                    ? "/// <para/>"
                    : "/// " + NameUtils.EscapeXmlDoc(line));
            }

            Line("/// </remarks>");
        }

        public override string ToString() => _sb.ToString();
    }
}
