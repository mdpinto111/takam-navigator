using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Takam.Core.Text;

/// <summary>
/// Normalisation du texte hébreu pour la recherche par mots.
/// Problèmes traités : guillemets d'abréviation (תכ"ם / תכ״ם / תכ''ם), voyelles (ניקוד),
/// caractères invisibles de direction, et préfixes collés au mot (ו, ה, ב, ל, מ, ש, כ).
/// </summary>
public static partial class HebrewText
{
    /// <summary>Lettres pouvant être un préfixe grammatical collé au mot.</summary>
    private const string PrefixLetters = "והבלמשכ";
    private const int MinStemLength = 3;
    private const int MaxPrefixLength = 3; // ex. "וכש", "שב", "והת"... on se limite à 3 lettres

    /// <summary>Mots vides : trop fréquents pour aider la recherche.</summary>
    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "של", "את", "על", "עם", "או", "גם", "כי", "אם", "לא", "כן", "יש", "אין", "זה", "זו", "זאת", "הוא", "היא",
        "הם", "הן", "אני", "אנחנו", "מה", "מי", "איך", "איפה", "מתי", "למה", "כמה", "האם", "אשר", "כל", "רק",
        "עד", "אל", "אצל", "בין", "לפני", "אחרי", "תוך", "כדי", "לגבי", "צריך", "צריכים", "אפשר", "מותר",
        "עושים", "לעשות", "ה", "ו", "ב", "ל", "מ", "ש"
    };

    /// <summary>Supprime voyelles et caractères de contrôle, unifie guillemets et tirets, compacte les espaces.</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;           // ניקוד et autres marques
            if (cat == UnicodeCategory.Format) continue;                    // U+200E/U+200F, etc.
            sb.Append(ch switch
            {
                '״' or '“' or '”' or '„' => '"',    // gershayim, guillemets typographiques
                '׳' or '‘' or '’' or '`' => '\'',         // geresh
                '־' or '–' or '—' => '-',                  // maqaf, tirets
                ' ' => ' ',
                _ => ch
            });
        }
        var s = sb.ToString().Normalize(NormalizationForm.FormC);
        s = s.Replace("''", "\"");
        // Guillemets ou apostrophe à l'intérieur d'un mot hébreu = abréviation : תכ"ם -> תכם, מט"ח -> מטח
        s = InWordQuote().Replace(s, "");
        return Spaces().Replace(s, " ").Trim();
    }

    /// <summary>Découpe un texte normalisé en mots (lettres et chiffres, les nombres décimaux restent entiers).</summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        foreach (Match m in Token().Matches(Normalize(text)))
            yield return m.Value.ToLowerInvariant();
    }

    /// <summary>
    /// Formes de recherche d'un mot : le mot lui-même et ses versions sans préfixe(s).
    /// Ex. "והתשלום" → { "והתשלום", "התשלום", "תשלום" }.
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

    /// <summary>Texte d'indexation : chaque mot suivi de ses variantes sans préfixe.</summary>
    public static string ToIndexText(string? text) =>
        string.Join(' ', Tokenize(text).SelectMany(Variants));

    /// <summary>Mots significatifs d'une requête (normalisés, sans mots vides, dédupliqués).</summary>
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
