using Takam.Core.Model;

namespace Takam.Core.Storage;

/// <summary>
/// Prépare les textes envoyés au modèle d'embeddings. Le modèle lit au plus 512 jetons : une section longue
/// est coupée en morceaux qui se chevauchent d'une ligne. Chaque morceau rappelle la directive et le chapitre,
/// pour garder le contexte (ex. "מועדי תשלום | הנחיות לביצוע | ...").
/// </summary>
public static class SectionChunker
{
    public const int DefaultMaxChars = 1200;

    public static IReadOnlyList<string> Chunks(Directive directive, Section section, int maxChars = DefaultMaxChars)
    {
        var prefix = string.Join(" | ", new[] { directive.Name, section.Heading }.Where(s => !string.IsNullOrWhiteSpace(s))) + " | ";
        var budget = Math.Max(200, maxChars - prefix.Length);
        var lines = section.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(l => SplitLongLine(l, budget)).ToList();

        var chunks = new List<string>();
        var current = new List<string>();
        var length = 0;
        foreach (var line in lines)
        {
            if (length + line.Length > budget && current.Count > 0)
            {
                chunks.Add(prefix + string.Join('\n', current));
                var overlap = current[^1];
                current = overlap.Length + line.Length <= budget ? [overlap] : [];
                length = current.Sum(c => c.Length);
            }
            current.Add(line);
            length += line.Length;
        }
        if (current.Count > 0) chunks.Add(prefix + string.Join('\n', current));
        if (chunks.Count == 0) chunks.Add(prefix.TrimEnd(' ', '|'));
        return chunks;
    }

    private static IEnumerable<string> SplitLongLine(string line, int budget)
    {
        if (line.Length <= budget) { yield return line; yield break; }
        var words = line.Split(' ');
        var part = new List<string>();
        var len = 0;
        foreach (var w in words)
        {
            if (len + w.Length + 1 > budget && part.Count > 0)
            {
                yield return string.Join(' ', part);
                part.Clear(); len = 0;
            }
            part.Add(w); len += w.Length + 1;
        }
        if (part.Count > 0) yield return string.Join(' ', part);
    }
}
