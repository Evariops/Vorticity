using System.Text;

namespace Vorticity.Generators;

/// <summary>Indented C# text.</summary>
internal sealed class SourceWriter
{
    private readonly StringBuilder _text = new StringBuilder();
    private int _depth;

    public void Line(string line = "")
    {
        if (line.Length > 0)
        {
            _text.Append(' ', _depth * 4).Append(line);
        }

        _text.Append('\n');
    }

    public void Open(string line)
    {
        Line(line);
        Line("{");
        _depth++;
    }

    /// <summary>Indents what follows, after a brace the caller wrote.</summary>
    public void Indent() => _depth++;

    public void Close(string suffix = "")
    {
        _depth--;
        Line("}" + suffix);
    }

    public override string ToString() => _text.ToString();
}
