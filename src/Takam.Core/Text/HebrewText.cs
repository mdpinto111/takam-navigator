using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Takam.Core.Text;

/// <summary>
/// Hebrew text normalization for keyword search.
/// Handles: abbreviation quotes (תכ"ם / תכ״ם / תכ''ם), vowel points (ניקוד),
/// invisible direction marks, and prefixes attached to the word (ו, ה, ב, ל, מ, ש, כ).
/// </summary>
public static partial class HebrewText
{
    /// <summary>Letters that can be a grammatical prefix attached to the word.</summary>
    private const string PrefixLetters = "והבלמשכ";
    private const int MinStemLength = 3;
    private const int MaxPrefixLength = 3; // e.g. "וכש", "שב", "והת"... limited to 3 letters

    /// <summary>Stop words: too frequent to help the search.</summary>
    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "של", "את", "על", "עם", "או", "גם", "כי", "אם", "לא", "כן", "יש", "אין", "זה", "זו", "זאת", "הוא", "היא",
        "הם", "הן", "אני", "אנחנו", "מה", "מי", "איך", "איפה", "מתי", "למה", "כמה", "האם", "אשר", "כל", "רק",
        "עד", "אל", "אצל", "בין", "לפני", "אחרי", "תוך", "כדי", "לגבי", "צריך", "צריכים", "אפשר", "מותר",
        "עושים", "לעשות", "ה", "ו", "ב", "ל", "מ", "ש"
    };

    /// <summary>Removes vowel points and control characters, unifies quotes and dashes, collapses spaces.</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;           // ניקוד and other marks
            if (cat == UnicodeCategory.Format) continue;                    // U+200E/U+200F, etc.
            sb.Append(ch switch
            {
                '״' or '“' or '”' or '„' => '"',    // gershayim, typographic quotes
                '׳' or '‘' or '’' or '`' => '\'',         // geresh
                '־' or '–' or '—' => '-',                  // maqaf, dashes
                ' ' => ' ',
                _ => ch
            });
        }
        var s = sb.ToString().Normalize(NormalizationForm.FormC);
        s = s.Replace("''", "\"");
        // Quote or apostrophe inside a Hebrew word = abbreviation: תכ"ם -> תכם, מט"ח -> מטח
        s = InWordQuote().Replace(s, "");
        return Spaces().Replace(s, " ").Trim();
    }

    /// <summary>Splits a normalized text into words (letters and digits; decimal numbers stay whole).</summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        foreach (Match m in Token().Matches(Normalize(text)))
            yield return m.Value.ToLowerInvariant();
    }

    /// <summary>
    /// Search forms of a word: the word itself and its versions without prefix(es).
    /// E.g. "והתשלום" → { "והתשלום", "התשלום", "תשלום" }.
    /// </summary>
    public static IReadOnlyList<string> Variants(string token)
    {
        var result = new List<string> { token };
        if (!IsHebrew(token)) return result;
        var current = token;
        for (var i = 0; i < MaxPrefixLength; i++)
        {
            if (current.Length - 1 < MinStemLength || !PrefixLetters.Contains(current[0])) break;
            current = current[1..];
            result.Add(current);
        }
        return result;
    }

    /// <summary>Index text: each word followed by its prefix-less variants.</summary>
    public static string ToIndexText(string? text) =>
        string.Join(' ', Tokenize(text).SelectMany(Variants));

    /// <summary>Meaningful words of a query (normalized, without stop words, deduplicated).</summary>
    public static IReadOnlyList<string> QueryTerms(string? query) =>
        Tokenize(query).Where(t => !StopWords.Contains(t)).Distinct().ToList();

    public static bool IsHebrew(string s) => s.Any(c => c is >= 'א' and <= 'ת');

    [GeneratedRegex(@"(?<=[א-ת])[""'](?=[א-ת])")]
    private static partial Regex InWordQuote();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:\.[\p{N}]+)*")]
    private static partial Regex Token();
}
