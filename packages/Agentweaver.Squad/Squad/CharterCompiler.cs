using System.Globalization;
using System.Text;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;

namespace Agentweaver.Squad.Squad;

/// <summary>
/// Compiles charter markdown from catalog role templates or custom role definitions.
/// Output is guaranteed emoji-free.
/// </summary>
public sealed class CharterCompiler
{
    private readonly CatalogReader _catalog;

    public CharterCompiler(CatalogReader catalog)
    {
        _catalog = catalog;
    }

    public string Compile(string roleId, string allocatedName)
    {
        var role = _catalog.LoadRole(roleId)
            ?? throw new InvalidOperationException($"Unknown role id '{roleId}'.");

        var template = _catalog.LoadCharterTemplate(roleId);

        string charter;
        if (template is not null)
        {
            charter = template
                .Replace("{Name}", allocatedName)
                .Replace("{Role Title}", role.Title)
                .Replace("{Role summary}", role.Summary);
        }
        else
        {
            charter = Render(allocatedName, role.Title, role.Summary,
                role.Capabilities, role.Responsibilities, role.Boundaries);
        }

        EnsureNoEmojis(charter);
        return charter;
    }

    public string CompileCustom(string name, string roleTitle, string summary,
        IReadOnlyList<string> capabilities, IReadOnlyList<string> responsibilities,
        IReadOnlyList<string> boundaries)
    {
        var charter = Render(name, roleTitle, summary, capabilities, responsibilities, boundaries);
        EnsureNoEmojis(charter);
        return charter;
    }

    private static string Render(string name, string roleTitle, string summary,
        IReadOnlyList<string> capabilities, IReadOnlyList<string> responsibilities,
        IReadOnlyList<string> boundaries)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(name).Append(" \u2014 ").Append(roleTitle).Append("\n\n");
        sb.Append(summary).Append("\n\n");
        sb.Append("## Role\n\n");
        sb.Append(roleTitle).Append(" for this project.\n\n");

        sb.Append("## Capabilities\n\n");
        foreach (var c in capabilities)
            sb.Append("- ").Append(c).Append(": proficient\n");
        sb.Append('\n');

        sb.Append("## Responsibilities\n\n");
        foreach (var r in responsibilities)
            sb.Append("- ").Append(r).Append('\n');
        sb.Append('\n');

        sb.Append("## Boundaries\n\n");
        foreach (var b in boundaries)
            sb.Append("- ").Append(b).Append('\n');

        return sb.ToString();
    }

    private static void EnsureNoEmojis(string text)
    {
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            var cp = char.ConvertToUtf32(element, 0);
            if (IsEmojiCodePoint(cp))
                throw new InvalidOperationException("Charter content must not contain emojis.");
        }
    }

    private static bool IsEmojiCodePoint(int cp) =>
        (cp >= 0x1F300 && cp <= 0x1FAFF) ||   // symbols, pictographs, supplemental
        (cp >= 0x1F000 && cp <= 0x1F2FF) ||   // mahjong, dominoes, enclosed
        (cp >= 0x2600 && cp <= 0x27BF) ||     // misc symbols and dingbats
        (cp >= 0x1F1E6 && cp <= 0x1F1FF) ||   // regional indicators
        cp == 0x2B50 || cp == 0x2B55 ||       // star, circle
        (cp >= 0xFE00 && cp <= 0xFE0F) ||     // variation selectors
        cp == 0x200D ||                       // zero-width joiner
        (cp >= 0x2190 && cp <= 0x21FF && cp != 0x2014); // arrows (em dash 0x2014 excluded, not in range anyway)
}
