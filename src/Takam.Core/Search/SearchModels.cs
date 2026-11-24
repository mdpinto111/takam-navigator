namespace Takam.Core.Search;

public enum SearchMode
{
    /// <summary>Keywords (BM25) + meaning (embeddings), merged with RRF. Default mode.</summary>
    Hybrid,
    Keyword,
    Semantic
}

public enum MatchKind
{
    /// <summary>The query is a directive number (e.g. 1.4.3).</summary>
    DirectiveNumber,
    Keyword,
    Semantic,
    Both
}

public sealed record SearchRequest(string Query, int Top = 5, SearchMode Mode = SearchMode.Hybrid, string? SubChapter = null);

public sealed record HighlightRange(int Start, int Length);

public sealed record SectionRef(string SectionId, string Label, string Heading);

public sealed record SearchHit
{
    public required string SectionId { get; init; }
    public required string DirectiveCode { get; init; }
    public required string DirectiveName { get; init; }
    public string? SubChapter { get; init; }
    public required string SectionLabel { get; init; }
    public required string SectionHeading { get; init; }
    public required string Snippet { get; init; }
    public required IReadOnlyList<HighlightRange> Highlights { get; init; }
    public int Edition { get; init; }
    public string? ValidFrom { get; init; }
    public required string OfficialUrl { get; init; }
    public required MatchKind MatchedBy { get; init; }
    public double Score { get; init; }
    /// <summary>Other relevant sections of the same directive.</summary>
    public IReadOnlyList<SectionRef> OtherSections { get; init; } = [];
}

public sealed record SearchResponse
{
    public required string Query { get; init; }
    public required SearchMode Mode { get; init; }
    public required bool Found { get; init; }
    /// <summary>Message to display when nothing is relevant enough.</summary>
    public string? Message { get; init; }
    public IReadOnlyList<SearchHit> Results { get; init; } = [];
    /// <summary>Diagnostics: best semantic similarity (null if embeddings are unavailable).</summary>
    public double? BestSemanticScore { get; init; }
    public bool SemanticAvailable { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class SearchOptions
{
    public const string SectionName = "Search";
    /// <summary>RRF constant k: score = Σ 1 / (k + rank).</summary>
    public int RrfK { get; set; } = 60;
    /// <summary>Number of candidates kept by each retriever before fusion.</summary>
    public int CandidatesPerRetriever { get; set; } = 30;
    // Confidence threshold (calibrated on data/eval, see README): a result is considered relevant if
    //   meaning >= StrongSemanticScore, or meaning >= MinSemanticScore AND coverage >= MinSupportingCoverage,
    //   or coverage >= MinKeywordCoverage.
    // Cosine alone is not enough: with e5 the scores are compressed (0.77–0.93) and an off-topic question
    // ("איך מחדשים דרכון") reaches 0.857, higher than a real question (0.845).
    /// <summary>Cosine similarity that is sufficient on its own.</summary>
    public double StrongSemanticScore { get; set; } = 0.90;
    /// <summary>Minimum cosine similarity when at least part of the question's words are also present.</summary>
    public double MinSemanticScore { get; set; } = 0.82;
    /// <summary>Share of the question's words that must support a semantic result.</summary>
    public double MinSupportingCoverage { get; set; } = 0.30;
    /// <summary>Share of the question's words that is sufficient on its own (keyword search).</summary>
    public double MinKeywordCoverage { get; set; } = 0.6;
    public int MaxTop { get; set; } = 20;
    public int MaxQueryLength { get; set; } = 300;
    public int SnippetLength { get; set; } = 240;
    public string OfficialBaseUrl { get; set; } = "https://takam.mof.gov.il";
}
