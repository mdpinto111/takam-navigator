using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Takam.Core.Model;
using Takam.Core.Parsing;

namespace Takam.Tests;

/// <summary>Section extraction, on the real sample files and on documents built for the test.</summary>
public class DocxSectionParserTests
{
    private static IReadOnlyList<Section> Parse(string code) => new DocxSectionParser().Parse(code, TestPaths.Docx(code));

    [Fact]
    public void Payment_dates_directive_has_expected_sections()
    {
        var sections = Parse("1.4.3");
        var s21 = Assert.Single(sections, s => s.Label == "2.1");
        Assert.Equal("הנחיות לביצוע", s21.Heading);
        Assert.Contains("לא יאוחר מ-45 ימים", s21.Text);
        var s15 = Assert.Single(sections, s => s.Label == "1.5");
        Assert.Contains("1.5.1", s15.Text);             // sub-items stay in their section
        Assert.Contains(sections, s => s.Label == "2.12");
    }

    [Fact]
    public void Word_field_codes_are_not_in_text()
    {
        var all = string.Join("\n", Parse("1.4.3").Select(s => s.Text));
        Assert.DoesNotContain("REF _Ref", all);
        Assert.DoesNotContain("MERGEFORMAT", all);
        Assert.DoesNotContain('‏', all);
    }

    [Fact]
    public void Change_log_annex_is_skipped_but_definitions_are_kept()
    {
        var annexes = Parse("1.4.3").Where(s => s.Kind == SectionKind.Annex).ToList();
        Assert.Contains(annexes, a => a.Heading == "הגדרות");
        Assert.DoesNotContain(annexes, a => a.Heading.Contains("טבלת שינויים"));
    }

    [Fact]
    public void Directive_without_numbering_becomes_a_single_section()
    {
        var s = Assert.Single(Parse("1.3.3"));
        Assert.Equal(SectionKind.Whole, s.Kind);
        Assert.Contains("כתבי שיפוי", s.Text);
    }

    [Fact]
    public void Headings_numbered_through_their_style_give_chapter_numbers()
    {
        // 1.6.5: "מבוא" and "הנחיות לביצוע" are numbered by two different lists; Word shows 1. then 2.
        var labels = Parse("1.6.5").Where(s => s.Kind == SectionKind.Body).Select(s => s.Label).ToList();
        Assert.Equal("1.1", labels[0]);
        Assert.Contains("2.1", labels);
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void Section_ids_are_unique_in_every_sample_directive()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(TestPaths.Sample, "docx"), "*.docx"))
        {
            var sections = new DocxSectionParser().Parse(Path.GetFileNameWithoutExtension(file), file);
            Assert.NotEmpty(sections);
            Assert.Equal(sections.Count, sections.Select(s => s.Id).Distinct().Count());
            Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
        }
    }

    [Fact]
    public void Table_content_is_extracted()
    {
        using var ms = BuildDocx(body =>
        {
            body.Append(NumberedParagraph("מבוא", level: 0));
            body.Append(NumberedParagraph("פסקה ראשונה", level: 1));
            body.Append(new Table(new TableRow(Cell("סוג תשלום"), Cell("45 ימים"))));
        });
        var s = Assert.Single(new DocxSectionParser().Parse("9.9.9", ms));
        Assert.Equal("1.1", s.Label);
        Assert.Contains("סוג תשלום | 45 ימים", s.Text);
    }

    [Fact]
    public void Empty_document_gives_no_sections()
    {
        using var ms = BuildDocx(_ => { });
        Assert.Empty(new DocxSectionParser().Parse("9.9.9", ms));
    }

    [Fact]
    public void Corrupted_file_throws_a_clear_error()
    {
        using var ms = new MemoryStream("this is not a docx"u8.ToArray());
        Assert.ThrowsAny<Exception>(() => new DocxSectionParser().Parse("9.9.9", ms));
    }

    // ------------------------------------------------------------- building small test .docx files

    private static TableCell Cell(string text) => new(new Paragraph(new Run(new Text(text))));

    private static Paragraph NumberedParagraph(string text, int level) => new(
        new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = 1 })),
        new Run(new Text(text)));

    private static MemoryStream BuildDocx(Action<Body> fill)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var numbering = main.AddNewPart<NumberingDefinitionsPart>();
            numbering.Numbering = new Numbering(
                new AbstractNum(
                    new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new LevelText { Val = "%1." }) { LevelIndex = 0 },
                    new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new LevelText { Val = "%1.%2." }) { LevelIndex = 1 })
                { AbstractNumberId = 0 },
                new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
            fill(main.Document.Body!);
            main.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }
}
