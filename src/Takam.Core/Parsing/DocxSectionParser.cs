using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Takam.Core.Model;


namespace Takam.Core.Parsing;

/// <summary>
/// Extracts the text of a directive (.docx) and splits it into numbered sections.
/// The numbering (1, 1.1, 2.12...) is not in the text: Word generates it from the styles and the
/// list definitions. We recompute it the way Word does so that the numbers match the document.
/// </summary>
public sealed partial class DocxSectionParser
{
    /// <summary>A paragraph (or a table) with its computed numbering.</summary>
    public sealed record Block(string Text, string? Label, int? Level, bool IsTable);

    public IReadOnlyList<Section> Parse(string directiveCode, Stream docx)
    {
        var blocks = ReadBlocks(docx);
        return BuildSections(directiveCode, blocks);
    }

    public IReadOnlyList<Section> Parse(string directiveCode, string path)
    {
        using var fs = File.OpenRead(path);
        return Parse(directiveCode, fs);
    }

    // ------------------------------------------------------------------ reading and numbering

    public IReadOnlyList<Block> ReadBlocks(Stream docx)
    {
        using var doc = WordprocessingDocument.Open(docx, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("Word document has no main part.");
        var numbering = new NumberingResolver(main);
        var body = main.Document?.Body ?? throw new InvalidDataException("Word document has no body.");

        var blocks = new List<Block>();
        foreach (var element in Flatten(body))
        {
            switch (element)
            {
                case Paragraph p:
                    var text = CleanText(ParagraphText(p));
                    if (text.Length == 0) continue;
                    var (label, level) = numbering.Next(p);
                    blocks.Add(new Block(text, label, level, false));
                    break;
                case Table t:
                    var tableText = TableText(t);
                    if (tableText.Length > 0) blocks.Add(new Block(tableText, null, null, true));
                    break;
            }
        }
        return blocks;
    }

    /// <summary>Top-level paragraphs and tables, walking through content controls (sdt).</summary>
    private static IEnumerable<OpenXmlElement> Flatten(OpenXmlElement parent)
    {
        foreach (var child in parent.ChildElements)
        {
            if (child is Paragraph or Table) yield return child;
            else if (child is SdtBlock sdt && sdt.SdtContentBlock is { } content)
                foreach (var inner in Flatten(content)) yield return inner;
        }
    }

    /// <summary>
    /// Visible text of a paragraph. Only w:t elements (displayed text) are read: field codes
    /// (w:instrText, e.g. "REF _Ref486944326 \r \h") and deleted text (w:delText) are excluded by design.
    /// </summary>
    private static string ParagraphText(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var e in p.Descendants())
        {
            switch (e)
            {
                case DocumentFormat.OpenXml.Wordprocessing.Text t: sb.Append(t.Text); break;
                case TabChar or Break or CarriageReturn: sb.Append(' '); break;
            }
        }
        return sb.ToString();
    }

    private static string TableText(Table table)
    {
        var lines = new List<string>();
        foreach (var row in table.Descendants<TableRow>())
        {
            var cells = row.Elements<TableCell>()
                .Select(c => CleanText(string.Join(' ', c.Descendants<Paragraph>().Select(ParagraphText))))
                .Where(c => c.Length > 0);
            var line = string.Join(" | ", cells);
            if (line.Length > 0) lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    /// <summary>Removes direction marks, leftover field codes and extra spaces.</summary>
    public static string CleanText(string s)
    {
        s = s.Replace("‎", "").Replace("‏", "").Replace("‫", "").Replace("‬", "").Replace(' ', ' ');
        s = FieldCodeRemnant().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    // ------------------------------------------------------------------ splitting into sections

    private static readonly Regex AnnexStart = AnnexStartRegex();

    public IReadOnlyList<Section> BuildSections(string code, IReadOnlyList<Block> blocks)
    {
        // Directive with no outline numbering at all (e.g. 1.3.3: a single cross-reference paragraph): one section.
        if (blocks.Count > 0 && blocks.All(b => b.Level is null) && !blocks.Any(b => !b.IsTable && AnnexStart.IsMatch(b.Text)))
            return [new Section
            {
                Id = Section.MakeId(code, 0), DirectiveCode = code, Order = 0, Label = "", Heading = "",
                Text = string.Join('\n', blocks.Select(x => x.Text)).Trim(), Kind = SectionKind.Whole
            }];

        var sections = new List<Section>();
        string heading = "";
        string? headingLabel = null;
        Draft? current = null;
        var inAnnex = false;
        var skipAnnex = false;

        void Close()
        {
            if (current is null) return;
            if (!skipAnnex && current.Text.Length > 0)
                sections.Add(new Section
                {
                    Id = Section.MakeId(code, sections.Count),
                    DirectiveCode = code,
                    Order = sections.Count,
                    Label = current.Label,
                    Heading = current.Heading,
                    Text = current.Text.ToString().Trim(),
                    Kind = current.Kind
                });
            current = null;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];

            // Start of an annex: a short paragraph "נספח א" alone on its line.
            if (!b.IsTable && AnnexStart.Match(b.Text) is { Success: true } annex)
            {
                Close();
                inAnnex = true;
                var title = i + 1 < blocks.Count && !blocks[i + 1].IsTable && blocks[i + 1].Label is null ? blocks[i + 1].Text : "";
                // The change log does not help find a rule: it is skipped.
                skipAnnex = title.Contains("טבלת שינויים") || title.Contains("טבלת השינויים");
                current = new Draft(annex.Value.Trim(), title, SectionKind.Annex);
                if (title.Length > 0) i++; // the title is carried by "Heading"
                continue;
            }

            if (inAnnex)
            {
                current!.Append(b.Label is null ? b.Text : $"{b.Label}. {b.Text}");
                continue;
            }

            if (b.Level == 0)
            {
                Close();
                heading = b.Text;
                headingLabel = b.Label;
                continue;
            }

            if (b.Level == 1)
            {
                Close();
                current = new Draft(b.Label!, heading, SectionKind.Body);
                current.Append(b.Text);
                continue;
            }

            // Unnumbered text or sub-level: attached to the current section.
            current ??= new Draft(headingLabel ?? "0", heading, SectionKind.Body);
            current.Append(b.Label is null || b.IsTable ? b.Text : $"{b.Label}. {b.Text}");
        }
        Close();

        if (sections.Count == 0 && blocks.Count > 0)
        {
            // Directive without structure (e.g. 1.3.3: a single cross-reference paragraph).
            sections.Add(new Section
            {
                Id = Section.MakeId(code, 0), DirectiveCode = code, Order = 0, Label = "",
                Heading = "", Text = string.Join('\n', blocks.Select(x => x.Text)), Kind = SectionKind.Whole
            });
        }
        return sections;
    }

    private sealed class Draft(string label, string heading, SectionKind kind)
    {
        public string Label { get; } = label;
        public string Heading { get; } = heading;
        public SectionKind Kind { get; } = kind;
        public StringBuilder Text { get; } = new();
        public void Append(string line) => Text.Append(line).Append('\n');
    }

    [GeneratedRegex(@"^נספח\s+[א-ת]{1,2}['׳]?\s*$")]
    private static partial Regex AnnexStartRegex();

    [GeneratedRegex(@"\b(?:REF|PAGEREF|HYPERLINK)\s+_?\w+(?:\s+\\[a-z*]+(?:\s+MERGEFORMAT)?)*")]
    private static partial Regex FieldCodeRemnant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // ------------------------------------------------------------------ Word numbering computation

    /// <summary>
    /// Reproduces Word's automatic numbering. A paragraph is numbered:
    /// (1) by its own properties (w:numPr), (2) by its style or a parent style (basedOn),
    /// (3) or by a list level that points to its style (w:lvl/w:pStyle).
    /// Headings and sections form an "outline": Word numbers them as a single hierarchy even when they
    /// are defined in different lists (checked with Word on 1.10.1 and 1.6.5). So we keep one shared outline
    /// counter for these levels, and per-list counters for plain numbered lists.
    /// A level resets the levels below it.
    /// </summary>
    private sealed partial class NumberingResolver
    {
        private sealed record StyleInfo(string? BasedOn, string? NumId, int? Level, int? OutlineLevel);
        private sealed record LevelInfo(string? Text, string? Format, int Start);

        private readonly Dictionary<string, StyleInfo> _styles = new();
        private readonly Dictionary<string, string> _numToAbstract = new();
        private readonly Dictionary<(string Abs, int Level), LevelInfo> _levels = new();
        private readonly Dictionary<string, (string Abs, int Level)> _levelByStyle = new();
        private readonly Dictionary<string, int[]> _counters = new();
        private readonly int[] _outline = new int[9];

        public NumberingResolver(MainDocumentPart main)
        {
            foreach (var s in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
            {
                if (s.StyleId?.Value is not { } id) continue;
                var numPr = s.StyleParagraphProperties?.NumberingProperties;
                _styles[id] = new StyleInfo(s.BasedOn?.Val?.Value, numPr?.NumberingId?.Val?.Value.ToString(),
                    numPr?.NumberingLevelReference?.Val?.Value, s.StyleParagraphProperties?.OutlineLevel?.Val?.Value);
            }
            var numbering = main.NumberingDefinitionsPart?.Numbering;
            if (numbering is null) return;
            foreach (var n in numbering.Elements<NumberingInstance>())
                if (n.NumberID?.Value is { } nid && n.AbstractNumId?.Val?.Value is { } aid)
                    _numToAbstract[nid.ToString()] = aid.ToString();
            foreach (var a in numbering.Elements<AbstractNum>())
            {
                var aid = a.AbstractNumberId?.Value.ToString();
                if (aid is null) continue;
                foreach (var l in a.Elements<Level>())
                {
                    var ilvl = l.LevelIndex?.Value ?? 0;
                    _levels[(aid, ilvl)] = new LevelInfo(l.LevelText?.Val?.Value, l.NumberingFormat?.Val?.InnerText,
                        l.StartNumberingValue?.Val?.Value ?? 1);
                    if (l.ParagraphStyleIdInLevel?.Val?.Value is { } styleId)
                        _levelByStyle.TryAdd(styleId, (aid, ilvl));
                }
            }
        }

        public (string? Label, int? Level) Next(Paragraph p)
        {
            var (abs, level) = Resolve(p);
            if (abs is null || !_levels.TryGetValue((abs, level), out var info)) return (null, null);
            if (info.Format != "decimal" || string.IsNullOrEmpty(info.Text) || !info.Text.Contains('%')) return (null, null);

            int[] counters;
            bool outline = IsOutline(p, abs, level, info);
            if (outline)
            {
                counters = _outline;
                counters[level]++;
                for (var k = level + 1; k < 9; k++) counters[k] = 0;
            }
            else
            {
                if (!_counters.TryGetValue(abs, out counters!))
                {
                    counters = new int[9];
                    for (var i = 0; i < 9; i++)
                        counters[i] = (_levels.TryGetValue((abs, i), out var li) ? li.Start : 1) - 1;
                    _counters[abs] = counters;
                }
                counters[level]++;
                for (var k = level + 1; k < 9; k++)
                    counters[k] = (_levels.TryGetValue((abs, k), out var lk) ? lk.Start : 1) - 1;
            }

            var label = PlaceHolder().Replace(info.Text, m =>
                Math.Max(counters[int.Parse(m.Groups[1].Value) - 1], 1).ToString()).TrimEnd('.', ')');
            // A plain numbered list is not an outline level: its number stays in the text.
            return (label, outline ? level : null);
        }

        /// <summary>
        /// Outline level: top-level heading (list level linked to a style, or a style with outline level 0)
        /// or a sub-level whose number includes the parent's (e.g. "%1.%2.").
        /// </summary>
        private bool IsOutline(Paragraph p, string abs, int level, LevelInfo info)
        {
            if (level > 0) return info.Text!.Contains("%1");
            if (_levelByStyle.Any(kv => kv.Value == (abs, 0))) return true;
            // Hierarchical list whose level 2 includes the level 1 number ("%1.%2."): it is an outline.
            if (_levels.TryGetValue((abs, 1), out var sub) && sub.Text?.Contains("%1") == true) return true;
            var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            for (var guard = 0; styleId is not null && guard < 15; guard++)
            {
                if (!_styles.TryGetValue(styleId, out var st)) break;
                if (st.OutlineLevel is { } ol) return ol == 0;
                styleId = st.BasedOn;
            }
            return p.ParagraphProperties?.OutlineLevel?.Val?.Value == 0;
        }

        private (string? Abs, int Level) Resolve(Paragraph p)
        {
            var pPr = p.ParagraphProperties;
            var numPr = pPr?.NumberingProperties;
            var styleId = pPr?.ParagraphStyleId?.Val?.Value;

            // (1) numbering set on the paragraph itself
            if (numPr?.NumberingId?.Val?.Value is { } pNum)
            {
                if (pNum == 0) return (null, 0); // numId 0 = numbering explicitly removed
                var lvl = numPr.NumberingLevelReference?.Val?.Value ?? StyleLevel(styleId) ?? 0;
                return (_numToAbstract.GetValueOrDefault(pNum.ToString()), lvl);
            }

            // (2) and (3): walk up the style chain
            int? inheritedLevel = numPr?.NumberingLevelReference?.Val?.Value;
            var sid = styleId ?? "Normal";
            for (var guard = 0; sid is not null && guard < 15; guard++)
            {
                if (_styles.TryGetValue(sid, out var st))
                {
                    inheritedLevel ??= st.Level;
                    if (st.NumId is { } numId)
                    {
                        if (numId == "0") return (null, 0);
                        var abs = _numToAbstract.GetValueOrDefault(numId);
                        var lvl = inheritedLevel ?? (_levelByStyle.TryGetValue(sid, out var bl) ? bl.Level : 0);
                        return (abs, lvl);
                    }
                }
                if (_levelByStyle.TryGetValue(sid, out var byStyle))
                    return (byStyle.Abs, inheritedLevel ?? byStyle.Level);
                sid = st?.BasedOn;
            }
            return (null, 0);
        }

        private int? StyleLevel(string? styleId)
        {
            for (var guard = 0; styleId is not null && guard < 15; guard++)
            {
                if (!_styles.TryGetValue(styleId, out var st)) return null;
                if (st.Level is { } l) return l;
                styleId = st.BasedOn;
            }
            return null;
        }

        [GeneratedRegex(@"%(\d)")]
        private static partial Regex PlaceHolder();
    }
}
