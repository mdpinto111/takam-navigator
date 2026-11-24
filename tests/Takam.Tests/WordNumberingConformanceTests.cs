using System.Text.Json;
using Takam.Core.Model;
using Takam.Core.Parsing;

namespace Takam.Tests;

/// <summary>
/// Les numéros de section calculés doivent être ceux que Word affiche.
/// Référence : Fixtures/word-section-labels.json, exporté de Word (ListFormat.ListString) pour les 34 directives
/// de l'échantillon. Les numéros calculés doivent être le début de la liste de Word (Word continue ensuite
/// avec la numérotation interne des annexes, que nous traitons à part).
/// </summary>
public class WordNumberingConformanceTests
{
    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var code in Reference().Keys.Where(c => c != "1.10.2")) data.Add(code);
        return data;
    }

    private static Dictionary<string, List<string>> Reference() =>
        JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(TestPaths.Fixture("word-section-labels.json")))!;

    /// <summary>
    /// 1.10.2 : Word y affiche lui-même des doublons (2.4, 2.4, 2.5, 2.5…), il ne peut pas servir de référence.
    /// On vérifie que notre numérotation est continue : 1.1–1.4, 2.1–2.10, 3.1–3.2, 4.1–4.2.
    /// </summary>
    [Fact]
    public void Directive_1_10_2_is_numbered_continuously()
    {
        var labels = new DocxSectionParser().Parse("1.10.2", TestPaths.Docx("1.10.2"))
            .Where(s => s.Kind == SectionKind.Body).Select(s => s.Label);
        string[] expected = [.. Seq(1, 4), .. Seq(2, 10), .. Seq(3, 2), .. Seq(4, 2)];
        Assert.Equal(expected, labels);
        static IEnumerable<string> Seq(int chapter, int n) => Enumerable.Range(1, n).Select(i => $"{chapter}.{i}");
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Section_labels_match_word(string code)
    {
        var expected = Reference()[code];
        var actual = new DocxSectionParser().Parse(code, TestPaths.Docx(code))
            .Where(s => s.Kind == SectionKind.Body && s.Label.Contains('.')).Select(s => s.Label).ToList();
        // Défaut connu du modèle Word de 1.10.1 et 1.10.2 : le premier point de chaque chapitre s'affiche "2." au lieu
        // de "2.1" (absent de la référence, qui ne garde que les numéros N.M). Nous le numérotons N.1, plus utile pour citer.
        var shownByWord = actual.Where(expected.Contains).ToList();
        var notShown = actual.Except(shownByWord).ToList();
        Assert.All(notShown, label => Assert.EndsWith(".1", label));
        Assert.True(notShown.Count <= 4, $"Trop d'écarts avec Word : {string.Join(", ", notShown)}");
        Assert.Equal(expected.Take(shownByWord.Count), shownByWord);
    }
}
