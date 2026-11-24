using Takam.Core.Search;
using Takam.Core.Storage;

namespace Takam.Tests;

[Collection("search")]
public class HybridSearchEngineTests(SearchFixture fx)
{
    private HybridSearchEngine Engine(SearchOptions? o = null) => new(fx.Index, fx.Embeddings, o ?? new SearchOptions());

    [Fact]
    public void Keyword_search_finds_the_directive()
    {
        var r = Engine().Search(new SearchRequest("עיכוב בתשלום", Mode: SearchMode.Keyword));
        Assert.True(r.Found);
        Assert.Equal("1.4.3", r.Results[0].DirectiveCode);
        Assert.Equal(MatchKind.Keyword, r.Results[0].MatchedBy);
    }

    [Fact]
    public void Prefixed_words_match_their_stem()
    {
        // "ולתשלום" (préfixes ו + ל) doit retrouver "תשלום" ; la directive sur les avances s'appelle "תשלום מקדמות".
        var r = Engine().Search(new SearchRequest("ולתשלום מקדמות", Mode: SearchMode.Keyword));
        Assert.True(r.Found);
        Assert.Equal("1.4.5", r.Results[0].DirectiveCode);
    }

    [Theory]
    [InlineData("1.4.5")]
    [InlineData("H.1.4.5")]
    [InlineData("  1.4.5 ")]
    public void Directive_number_goes_straight_to_the_directive(string query)
    {
        var r = Engine().Search(new SearchRequest(query));
        var hit = Assert.Single(r.Results);
        Assert.Equal("1.4.5", hit.DirectiveCode);
        Assert.Equal(MatchKind.DirectiveNumber, hit.MatchedBy);
        Assert.StartsWith("2.", hit.SectionLabel);          // on ouvre sur "הנחיות לביצוע"
        Assert.NotEmpty(hit.OtherSections);
    }

    [Fact]
    public void Unknown_directive_number_is_not_found()
    {
        var r = Engine().Search(new SearchRequest("9.9.9"));
        Assert.False(r.Found);
        Assert.Contains("9.9.9", r.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_query_is_rejected(string query) =>
        Assert.Throws<ArgumentException>(() => Engine().Search(new SearchRequest(query)));

    [Fact]
    public void Too_long_query_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Engine().Search(new SearchRequest(new string('א', 301))));

    [Fact]
    public void Off_topic_query_returns_not_found()
    {
        var r = Engine().Search(new SearchRequest("מה מזג האוויר מחר בתל אביב"));
        Assert.False(r.Found);
        Assert.Empty(r.Results);
        Assert.NotNull(r.Message);
    }

    [Fact]
    public void Stop_words_only_query_returns_not_found() =>
        Assert.False(Engine().Search(new SearchRequest("מה האם של")).Found);

    [Fact]
    public void Results_are_grouped_one_per_directive()
    {
        var r = Engine().Search(new SearchRequest("תשלום לספק", Top: 10));
        Assert.True(r.Found);
        Assert.Equal(r.Results.Count, r.Results.Select(x => x.DirectiveCode).Distinct().Count());
        Assert.Contains(r.Results, x => x.OtherSections.Count > 0);
        Assert.All(r.Results, x => Assert.DoesNotContain(x.OtherSections, o => o.SectionId == x.SectionId));
    }

    [Fact]
    public void Top_is_clamped()
    {
        var r = Engine().Search(new SearchRequest("תשלום", Top: 500));
        Assert.True(r.Results.Count <= new SearchOptions().MaxTop);
    }

    [Fact]
    public void Sub_chapter_filter_restricts_results()
    {
        var sub = fx.Index.Directives["1.10.1"].SubChapter!;
        var r = Engine().Search(new SearchRequest("תשלום מסמכים כספיים", Top: 10, SubChapter: sub));
        Assert.All(r.Results, x => Assert.Equal(sub, x.SubChapter));
    }

    [Fact]
    public void Reference_lists_are_not_returned()
    {
        // "חוק מוסר תשלומים" figure dans la liste "מסמכים ישימים" de 1.4.3 : cette liste ne doit jamais être un résultat.
        var r = Engine().Search(new SearchRequest("חוק מוסר תשלומים", Top: 10, Mode: SearchMode.Keyword));
        Assert.DoesNotContain(r.Results, x => x.SectionHeading == "מסמכים ישימים");
    }

    [Fact]
    public void Without_model_search_falls_back_to_keywords()
    {
        using var index = SearchIndex.Open(fx.KeywordOnlyDatabasePath);
        var engine = new HybridSearchEngine(index, embeddings: null, new SearchOptions());
        Assert.False(engine.SemanticAvailable);
        var r = engine.Search(new SearchRequest("עיכוב בתשלום"));
        Assert.Equal(SearchMode.Keyword, r.Mode);
        Assert.Equal("1.4.3", r.Results[0].DirectiveCode);
        Assert.Throws<InvalidOperationException>(() => engine.Search(new SearchRequest("עיכוב", Mode: SearchMode.Semantic)));
    }

    [Fact]
    public void Index_built_with_another_model_disables_semantic_search()
    {
        var other = new OtherModel();
        var engine = new HybridSearchEngine(fx.Index, other, new SearchOptions());
        Assert.False(engine.SemanticAvailable);
    }

    [Fact]
    public void Hit_carries_edition_and_official_link()
    {
        var hit = Engine().Search(new SearchRequest("עיכוב בתשלום")).Results[0];
        Assert.True(hit.Edition > 0);
        Assert.StartsWith("https://takam.mof.gov.il/document/", hit.OfficialUrl);
    }

    [Fact]
    public void Confidence_rule_combines_meaning_and_words()
    {
        var e = Engine();
        Assert.True(e.IsConfident(0.91, 0));        // sens très fort
        Assert.True(e.IsConfident(0.85, 0.33));     // sens correct + une partie des mots
        Assert.False(e.IsConfident(0.857, 0.25));   // cas réel "איך מחדשים דרכון" : rejeté
        Assert.True(e.IsConfident(null, 0.75));     // mots seuls (sans modèle)
        Assert.False(e.IsConfident(null, 0.5));
    }

    private sealed class OtherModel : Takam.Core.Embeddings.IEmbeddingService
    {
        public string ModelId => "another-model";
        public int Dimensions => 128;
        public float[] EmbedQuery(string q) => new float[128];
        public float[] EmbedPassage(string p) => new float[128];
    }
}

public class ReciprocalRankFusionTests
{
    [Fact]
    public void Item_ranked_well_in_both_lists_wins()
    {
        var keyword = new List<(string, double)> { ("a", 9), ("b", 5), ("c", 1) };
        var semantic = new List<(string, double)> { ("b", 0.9), ("d", 0.8), ("a", 0.7) };
        var fused = HybridSearchEngine.ReciprocalRankFusion(keyword, semantic, 60);
        Assert.Equal(["b", "a", "d", "c"], fused.Select(x => x.SectionId));
    }

    [Fact]
    public void Scores_use_ranks_only()
    {
        var fused = HybridSearchEngine.ReciprocalRankFusion([("x", 1000.0)], [("y", 0.0001)], 60);
        Assert.Equal(fused[0].Score, fused[1].Score, 10);       // même rang = même score, quelle que soit l'échelle
    }

    [Fact]
    public void Empty_lists_give_empty_result() =>
        Assert.Empty(HybridSearchEngine.ReciprocalRankFusion([], [], 60));
}

public class SnippetBuilderTests
{
    [Fact]
    public void Highlights_point_to_query_words_including_prefixed_forms()
    {
        const string text = "פיגורים במועדי תשלום יישאו הפרשי הצמדה וריבית, כנדרש בחוק.";
        var (snippet, highlights) = SnippetBuilder.Build(text, ["מועדי", "ריבית"], 240);
        var words = highlights.Select(h => snippet.Substring(h.Start, h.Length)).ToList();
        Assert.Equal(["במועדי", "וריבית"], words);
    }

    [Fact]
    public void Long_text_is_cut_around_the_matches()
    {
        var text = string.Join(" ", Enumerable.Repeat("מילה", 200)) + " קופת ביטחון " + string.Join(" ", Enumerable.Repeat("סוף", 200));
        var (snippet, highlights) = SnippetBuilder.Build(text, ["קופת", "ביטחון"], 120);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.True(snippet.Length <= 124);
        Assert.Equal(["קופת", "ביטחון"], highlights.Select(h => snippet.Substring(h.Start, h.Length)));
    }

    [Fact]
    public void No_terms_gives_start_of_text_without_highlights()
    {
        var (snippet, highlights) = SnippetBuilder.Build(new string('א', 500), [], 100);
        Assert.Empty(highlights);
        Assert.Equal(101, snippet.Length);   // 100 caractères + "…"
    }
}
