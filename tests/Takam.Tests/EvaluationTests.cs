using Takam.Core.Configuration;
using Takam.Core.Embeddings;
using Takam.Core.Evaluation;
using Takam.Core.Search;
using Takam.Core.Site;
using Takam.Core.Storage;

namespace Takam.Tests;

/// <summary>
/// End-to-end measurement on data/eval/questions.json: the current site (faithful copy of its search) against our engine.
/// Engine tests are skipped if the database (data/takam.db) or the model is not present.
/// </summary>
public class EvaluationTests
{
    private static IReadOnlyList<EvalQuestion> Questions => EvalQuestion.Load(RepoPaths.Resolve("data/eval/questions.json"));
    private static SiteSearchReplica Site => SiteSearchReplica.Load(Path.Combine(TestPaths.Sample, "site-documents.json"));

    [Fact]
    public void Site_search_requires_all_words_in_one_field()
    {
        var site = new SiteSearchReplica([new() { Name = "מועדי תשלום", Keywords = "עיכוב בתשלום", DocumentType = "Instruction", NameCode = "1.4.3" }]);
        Assert.Single(site.Search("מועדי תשלום"));
        Assert.Single(site.Search("עיכוב"));
        Assert.Empty(site.Search("מועדי עיכוב"));      // words spread over two fields: not found
        Assert.Empty(site.Search("איחור בתשלום"));     // synonym: not found
    }

    [Fact]
    public void Current_site_finds_few_answers()
    {
        // Part 1 baseline: 2/20 with the question as typed, 8/20 with hand-picked keywords.
        var phrase = Questions.Count(q => q.ExpectedDirective is not null && Site.SearchDirectiveCodes(q.Query).Contains(q.ExpectedDirective));
        var keywords = Questions.Count(q => q.ExpectedDirective is not null && Site.SearchDirectiveCodes(q.Keywords ?? q.Query).Contains(q.ExpectedDirective));
        Assert.Equal(2, phrase);
        Assert.Equal(8, keywords);
    }

    [SkippableFact]
    public void Hybrid_search_finds_the_directive_and_rejects_off_topic()
    {
        var db = RepoPaths.Resolve("data/takam.db");
        Skip.IfNot(File.Exists(db) && File.Exists(TestPaths.Model), "Database or model missing: run download-model then 'build'.");
        using var index = SearchIndex.Open(db);
        using var embeddings = new E5OnnxEmbeddingService(TestPaths.Model, TestPaths.SentencePiece);
        var engine = new HybridSearchEngine(index, embeddings, new SearchOptions());
        Skip.IfNot(engine.SemanticAvailable, "Database built without embeddings.");

        var report = new Evaluator(index, engine, null).RunEngine(Questions, SearchMode.Hybrid);

        Assert.True(report.DirectiveTop5 >= 19, $"Directive in top 5: {report.DirectiveTop5}/{report.Answerable}");
        Assert.Equal(report.OffTopicIds.Count, report.OffTopicRejected);
    }
}
