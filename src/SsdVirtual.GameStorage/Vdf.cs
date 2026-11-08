using System.Text;

namespace SsdVirtual.GameStorage;

/// <summary>Minimal parser for Valve KeyValues text files (appmanifest_*.acf, libraryfolders.vdf).</summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
    public VdfNode? Child(string key) => Children.TryGetValue(key, out var c) ? c : null;

    public static VdfNode Parse(string text)
    {
        var pos = 0;
        var root = new VdfNode();
        ParseInto(root, text, ref pos, topLevel: true);
        return root;
    }

    private static void ParseInto(VdfNode node, string text, ref int pos, bool topLevel)
    {
        while (true)
        {
            var key = NextToken(text, ref pos);
            if (key == null) return;
            if (key == "}") { if (topLevel) continue; return; }
            var next = NextToken(text, ref pos);
            if (next == null) return;
            if (next == "{")
            {
                var child = new VdfNode();
                ParseInto(child, text, ref pos, false);
                node.Children[key] = child;
            }
            else node.Values[key] = next;
        }
    }

    private static string? NextToken(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            if (c is '{' or '}') { pos++; return c.ToString(); }
            if (c == '"')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length)
                    {
                        pos++;
                        sb.Append(text[pos] switch { 'n' => '\n', 't' => '\t', _ => text[pos] });
                    }
                    else sb.Append(text[pos]);
                    pos++;
                }
                pos++;
                return sb.ToString();
            }
            var start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not '{' and not '}' and not '"') pos++;
            return text[start..pos];
        }
        return null;
    }
}
