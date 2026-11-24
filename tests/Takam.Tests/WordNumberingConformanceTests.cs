using System.Text.Json;
using Takam.Core.Model;
using Takam.Core.Parsing;

namespace Takam.Tests;

/// <summary>
/// The computed section numbers must be the ones Word displays.
/// Reference: Fixtures/word-section-labels.json, exported from Word (ListFormat.ListString) for the 34 directives
/// of the sample. The computed numbers must be the beginning of Word's list (Word then continues
/// with the annexes' internal numbering, which we handle separately).
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
    /// 1.10.2: Word itself shows duplicates there (2.4, 2.4, 2.5, 2.5…), so it cannot be the reference.
    /// We check that our numbering is continuous: 1.1–1.4, 2.1–2.10, 3.1–3.2, 4.1–4.2.
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
        // Known defect in the Word template of 1.10.1 and 1.10.2: the first item of each chapter shows "2." instead
        // of "2.1" (missing from the reference, which keeps only N.M numbers). We number it N.1, more useful for citing.
        var shownByWord = actual.Where(expected.Contains).ToList();
        var notShown = actual.Except(shownByWord).ToList();
        Assert.All(notShown, label => Assert.EndsWith(".1", label));
        Assert.True(notShown.Count <= 4, $"Too many differences from Word: {string.Join(", ", notShown)}");
        Assert.Equal(expected.Take(shownByWord.Count), shownByWord);
    }
}
