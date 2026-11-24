using System.Text.Json;
using Takam.Core.Search;
using Takam.Core.Site;
using Takam.Core.Storage;

namespace Takam.Core.Evaluation;

/// <summary>A test question and its correct answer (data/eval/questions.json).</summary>
public sealed record EvalQuestion
{
    public required string Id { get; init; }
    public required string Query { get; init; }
    public string? Keywords { get; init; }
    public string? ExpectedDirective { get; init; }
    /// <summary>Phrase found in the correct section (more robust than a section number).</summary>
    public string? ExpectedSectionContains { get; init; }
    public string Kind { get; init; } = "";

    public static IReadOnlyList<EvalQuestion> Load(string path) =>
        JsonSerializer.Deserialize<List<EvalQuestion>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
}

public sealed record EvalOutcome(string QuestionId, bool DirectiveFound, bool SectionFound, int? Rank, string Detail);

public sealed record EvalReport(string System, IReadOnlyList<EvalOutcome> Outcomes)
{
    /// <summary>Questions that have a correct answer (off-topic questions excluded).</summary>
    public int Answerable { get; init; }
    public int DirectiveTop5 => Outcomes.Count(o => o.DirectiveFound && !OffTopicIds.Contains(o.QuestionId));
    public int SectionTop5 => Outcomes.Count(o => o.SectionFound && !OffTopicIds.Contains(o.QuestionId));
    /// <summary>Off-topic questions for which nothing was returned (expected).</summary>
    public int OffTopicRejected => Outcomes.Count(o => o.DirectiveFound && OffTopicIds.Contains(o.QuestionId));
    public IReadOnlySet<string> OffTopicIds { get; init; } = new HashSet<string>();
}

/// <summary>
/// Measures search quality: is the correct directive (and the correct section) in the top 5 results?
/// Off-topic questions must return nothing.
/// </summary>
public sealed class Evaluator(SearchIndex index, HybridSearchEngine engine, SiteSearchReplica? site)
{
    public const int TopK = 5;

    public EvalReport RunEngine(IReadOnlyList<EvalQuestion> questions, SearchMode mode, bool useKeywordsVariant = false)
    {
        var outcomes = new List<EvalOutcome>();
        foreach (var q in questions)
        {
            var text = useKeywordsVariant && q.Keywords is not null ? q.Keywords : q.Query;
            var response = engine.Search(new SearchRequest(text, TopK, mode));
            if (q.ExpectedDirective is null)
            {
                outcomes.Add(new EvalOutcome(q.Id, !response.Found, !response.Found, null,
                    response.Found ? $"should have found nothing, returned {response.Results[0].DirectiveCode}" : "nothing found (expected)"));
                continue;
            }
            var rank = response.Results.ToList().FindIndex(r => r.DirectiveCode == q.ExpectedDirective);
            var sectionFound = rank >= 0 && SectionMatches(q, response.Results[rank]);
            outcomes.Add(new EvalOutcome(q.Id, rank >= 0, sectionFound, rank >= 0 ? rank + 1 : null,
                string.Join(", ", response.Results.Select(r => $"{r.DirectiveCode}§{r.SectionLabel}"))));
        }
        return Report($"{mode}{(useKeywordsVariant ? " (keywords)" : "")}", questions, outcomes);
    }

    /// <summary>The site does not rank its results: the directive counts as found if it is in the list.</summary>
    public EvalReport RunSite(IReadOnlyList<EvalQuestion> questions, bool useKeywordsVariant)
    {
        ArgumentNullException.ThrowIfNull(site);
        var outcomes = new List<EvalOutcome>();
        foreach (var q in questions)
        {
            var text = useKeywordsVariant && q.Keywords is not null ? q.Keywords : q.Query;
            var codes = site.SearchDirectiveCodes(text);
            var all = site.Search(text).Count;
            var found = q.ExpectedDirective is null ? all == 0 : codes.Contains(q.ExpectedDirective);
            outcomes.Add(new EvalOutcome(q.Id, found, false, null, $"{all} result(s): {string.Join(", ", codes.Take(5))}"));
        }
        return Report($"Current site{(useKeywordsVariant ? " (keywords)" : " (sentence)")}", questions, outcomes);
    }

    private bool SectionMatches(EvalQuestion q, SearchHit hit)
    {
        if (q.ExpectedSectionContains is null) return true;
        var ids = new[] { hit.SectionId }.Concat(hit.OtherSections.Select(o => o.SectionId)).Take(1); // only the displayed section counts
        return ids.Any(id => index.Sections.TryGetValue(id, out var s) && s.Section.Text.Contains(q.ExpectedSectionContains));
    }

    private static EvalReport Report(string name, IReadOnlyList<EvalQuestion> questions, List<EvalOutcome> outcomes)
    {
        return new EvalReport(name, outcomes)
        {
            Answerable = questions.Count(q => q.ExpectedDirective is not null),
            OffTopicIds = questions.Where(q => q.ExpectedDirective is null).Select(q => q.Id).ToHashSet()
        };
    }
}
