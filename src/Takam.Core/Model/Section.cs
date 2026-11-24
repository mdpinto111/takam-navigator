namespace Takam.Core.Model;

public enum SectionKind
{
    Body,
    Annex,
    /// <summary>Directive sans sections numérotées (ex. 1.3.3, simple renvoi).</summary>
    Whole
}

/// <summary>Un sous-ensemble cohérent d'une directive : un sujet numéroté (ex. 2.1) avec tous ses sous-paragraphes.</summary>
public sealed record Section
{
    /// <summary>Identifiant stable : "{code}#{ordre}" (les numéros peuvent se répéter, ex. dans les annexes).</summary>
    public required string Id { get; init; }
    public required string DirectiveCode { get; init; }
    public required int Order { get; init; }
    /// <summary>Numéro affiché, ex. "2.1" ou "נספח א".</summary>
    public required string Label { get; init; }
    /// <summary>Titre du chapitre de premier niveau (ex. "הנחיות לביצוע") ou de l'annexe.</summary>
    public required string Heading { get; init; }
    public required string Text { get; init; }
    public SectionKind Kind { get; init; } = SectionKind.Body;

    public static string MakeId(string directiveCode, int order) => $"{directiveCode}#{order}";
}
