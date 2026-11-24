namespace Takam.Core.Model;

public enum SectionKind
{
    Body,
    Annex,
    /// <summary>Directive without numbered sections (e.g. 1.3.3, a simple cross-reference).</summary>
    Whole
}

/// <summary>A coherent part of a directive: a numbered topic (e.g. 2.1) with all its sub-paragraphs.</summary>
public sealed record Section
{
    /// <summary>Stable identifier: "{code}#{order}" (labels can repeat, e.g. in annexes).</summary>
    public required string Id { get; init; }
    public required string DirectiveCode { get; init; }
    public required int Order { get; init; }
    /// <summary>Displayed label, e.g. "2.1" or "נספח א".</summary>
    public required string Label { get; init; }
    /// <summary>Title of the top-level chapter (e.g. "הנחיות לביצוע") or of the annex.</summary>
    public required string Heading { get; init; }
    public required string Text { get; init; }
    public SectionKind Kind { get; init; } = SectionKind.Body;

    public static string MakeId(string directiveCode, int order) => $"{directiveCode}#{order}";
}
