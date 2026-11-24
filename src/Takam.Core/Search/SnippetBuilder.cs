using System.Text.RegularExpressions;
using Takam.Core.Text;

namespace Takam.Core.Search;

/// <summary>
/// Builds the displayed snippet: the window of text that contains the most query words,
/// with the positions of the words to highlight. A word in the text matches if it shares a prefix-less form
/// with a query word ("בתשלום" matches "תשלום").
/// </summary>
public static partial class SnippetBuilder
{
    public static (string Snippet, IReadOnlyList<HighlightRange> Highlights) Build(string text, IReadOnlyList<string> queryTerms, int length)
    {
        var flat = text.Replace('\n', ' ');
        var wanted = queryTerms.SelectMany(HebrewText.Variants).ToHashSet(StringComparer.Ordinal);
        var hits = new List<(int Start, int Length)>();
        if (wanted.Count > 0)
        {
            foreach (Match m in Word().Matches(flat))
            {
                var norm = HebrewText.Tokenize(m.Value).FirstOrDefault();
                if (norm is not null && HebrewText.Variants(norm).Any(wanted.Contains))
                    hits.Add((m.Index, m.Length));
            }
        }

        if (flat.Length <= length)
            return (flat, hits.Select(h => new HighlightRange(h.Start, h.Length)).ToList());

        // Window that covers the most matched words.
        var bestStart = 0; var bestCount = 0;
        for (var i = 0; i < hits.Count; i++)
        {
            var count = hits.Skip(i).TakeWhile(h => h.Start + h.Length <= hits[i].Start + length).Count();
            if (count > bestCount) { bestCount = count; bestStart = hits[i].Start; }
        }
        var start = Math.Max(0, bestStart - length / 5);
        start = AlignToWord(flat, start);
        var end = Math.Min(flat.Length, start + length);
        var snippet = (start > 0 ? "…" : "") + flat[start..end].Trim() + (end < flat.Length ? "…" : "");
        var offset = start - (start > 0 ? 1 : 0) + (flat[start..end].Length - flat[start..end].TrimStart().Length);
        var ranges = hits.Where(h => h.Start >= start && h.Start + h.Length <= end)
            .Select(h => new HighlightRange(h.Start - offset, h.Length)).ToList();
        return (snippet, ranges);
    }

    private static int AlignToWord(string s, int i)
    {
        while (i > 0 && i < s.Length && !char.IsWhiteSpace(s[i - 1])) i--;
        return i;
    }

    [GeneratedRegex(@"[\p{L}\p{N}""'״׳]+")]
    private static partial Regex Word();
}
