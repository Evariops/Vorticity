using System;
using System.Text;

namespace Vorticity.ObjectStorage.Xml;

/// <summary>The few writers the services' requests need: text escaped for an element.</summary>
internal static class XmlText
{
    /// <summary>
    /// Appends <paramref name="text"/> escaped for an element's content. A character XML 1.0 cannot
    /// carry, a control character but tab, newline and return, is refused: no escape writes it.
    /// </summary>
    /// <exception cref="ArgumentException">The text holds a character XML cannot carry.</exception>
    internal static StringBuilder AppendEscaped(this StringBuilder builder, string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '\r':
                    builder.Append("&#13;");
                    break;
                case < ' ' and not ('\t' or '\n'):
                case '￾' or '￿':
                    throw new ArgumentException($"A key holds the character U+{(int)c:X4}, which XML cannot carry in a request.", nameof(text));
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder;
    }
}
