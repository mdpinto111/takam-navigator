using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Takam.Core.Model;


namespace Takam.Core.Parsing;

/// <summary>
/// Extrait le texte d'une directive (.docx) et le découpe en sections numérotées.
/// La numérotation (1, 1.1, 2.12...) n'est pas dans le texte : Word la génère à partir des styles et des
/// définitions de listes. On la recalcule comme Word pour que les numéros correspondent à ceux du document.
/// </summary>
public sealed partial class DocxSectionParser
{
    /// <summary>Un paragraphe (ou un tableau) avec sa numérotation calculée.</summary>
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

    // ------------------------------------------------------------------ lecture et numérotation

    public IReadOnlyList<Block> ReadBlocks(Stream docx)
    {
        using var doc = WordprocessingDocument.Open(docx, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("Document Word sans partie principale.");
        var numbering = new NumberingResolver(main);
        var body = main.Document?.Body ?? throw new InvalidDataException("Document Word sans corps.");

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

    /// <summary>Paragraphes et tableaux de premier niveau, en traversant les contrôles de contenu (sdt).</summary>
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
    /// Texte visible d'un paragraphe. On ne lit que les éléments w:t (texte affiché) : les codes de champ
    /// (w:instrText, ex. "REF _Ref486944326 \r \h") et le texte supprimé (w:delText) sont exclus d'office.
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

    /// <summary>Supprime les caractères de direction, les restes de codes de champ et les espaces en trop.</summary>
    public static string CleanText(string s)
    {
        s = s.Replace("‎", "").Replace("‏", "").Replace("‫", "").Replace("‬", "").Replace(' ', ' ');
        s = FieldCodeRemnant().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    // ------------------------------------------------------------------ découpage en sections

    private static readonly Regex AnnexStart = AnnexStartRegex();

    public IReadOnlyList<Section> BuildSections(string code, IReadOnlyList<Block> blocks)
    {
        // Directive sans aucune numérotation de plan (ex. 1.3.3 : un seul paragraphe de renvoi) : une seule section.
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

            // Début d'annexe : un paragraphe court "נספח א" seul sur sa ligne.
            if (!b.IsTable && AnnexStart.Match(b.Text) is { Success: true } annex)
            {
                Close();
                inAnnex = true;
                var title = i + 1 < blocks.Count && !blocks[i + 1].IsTable && blocks[i + 1].Label is null ? blocks[i + 1].Text : "";
                // L'historique des modifications n'aide pas à trouver une règle : on l'ignore.
                skipAnnex = title.Contains("טבלת שינויים") || title.Contains("טבלת השינויים");
                current = new Draft(annex.Value.Trim(), title, SectionKind.Annex);
                if (title.Length > 0) i++; // le titre est porté par "Heading"
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

            // Texte sans numéro ou sous-niveau : rattaché à la section en cours.
            current ??= new Draft(headingLabel ?? "0", heading, SectionKind.Body);
            current.Append(b.Label is null || b.IsTable ? b.Text : $"{b.Label}. {b.Text}");
        }
        Close();

        if (sections.Count == 0 && blocks.Count > 0)
        {
            // Directive sans structure (ex. 1.3.3 : un seul paragraphe de renvoi).
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

    // ------------------------------------------------------------------ calcul de la numérotation Word

    /// <summary>
    /// Reproduit la numérotation automatique de Word. Un paragraphe est numéroté :
    /// (1) par ses propres propriétés (w:numPr), (2) par son style ou un style parent (basedOn),
    /// (3) ou par un niveau de liste qui désigne son style (w:lvl/w:pStyle).
    /// Les titres et sections forment un "plan" : Word les numérote comme une seule hiérarchie même quand ils
    /// sont définis dans des listes différentes (vérifié avec Word sur 1.10.1 et 1.6.5). On tient donc un compteur
    /// de plan commun pour ces niveaux, et des compteurs par liste pour les simples listes numérotées.
    /// Un niveau remet à zéro les niveaux inférieurs.
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
            // Une simple liste numérotée n'est pas un niveau de plan : on garde son numéro dans le texte.
            return (label, outline ? level : null);
        }

        /// <summary>
        /// Niveau de plan : titre de premier niveau (niveau de liste lié à un style, ou style de niveau hiérarchique 0)
        /// ou sous-niveau dont le numéro reprend celui du parent (ex. "%1.%2.").
        /// </summary>
        private bool IsOutline(Paragraph p, string abs, int level, LevelInfo info)
        {
            if (level > 0) return info.Text!.Contains("%1");
            if (_levelByStyle.Any(kv => kv.Value == (abs, 0))) return true;
            // Liste hiérarchique dont le niveau 2 reprend le numéro du niveau 1 ("%1.%2.") : c'est un plan.
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

            // (1) numérotation portée par le paragraphe
            if (numPr?.NumberingId?.Val?.Value is { } pNum)
            {
                if (pNum == 0) return (null, 0); // numId 0 = numérotation explicitement retirée
                var lvl = numPr.NumberingLevelReference?.Val?.Value ?? StyleLevel(styleId) ?? 0;
                return (_numToAbstract.GetValueOrDefault(pNum.ToString()), lvl);
            }

            // (2) et (3) : remonter la chaîne de styles
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
