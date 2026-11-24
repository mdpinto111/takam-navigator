using Takam.Core.Configuration;
using Takam.Core.Embeddings;
using Takam.Core.Evaluation;
using Takam.Core.Search;
using Takam.Core.Site;
using Takam.Core.Storage;

namespace Takam.Tests;

/// <summary>
/// Mesure de bout en bout sur data/eval/questions.json : le site actuel (copie fidèle de sa recherche) contre notre moteur.
/// Les tests du moteur sont ignorés si la base (data/takam.db) ou le modèle ne sont pas présents.
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
        Assert.Empty(site.Search("מועדי עיכוב"));      // mots répartis sur deux champs : pas trouvé
        Assert.Empty(site.Search("איחור בתשלום"));     // synonyme : pas trouvé
    }

    [Fact]
    public void Current_site_finds_few_answers()
    {
        // Mesure de référence de la partie 1 : 2/20 avec la question telle quelle, 8/20 avec des mots-clés choisis.
        var phrase = Questions.Count(q => q.ExpectedDirective is not null && Site.SearchDirectiveCodes(q.Query).Contains(q.ExpectedDirective));
        var keywords = Questions.Count(q => q.ExpectedDirective is not null && Site.SearchDirectiveCodes(q.Keywords ?? q.Query).Contains(q.ExpectedDirective));
        Assert.Equal(2, phrase);
        Assert.Equal(8, keywords);
    }

    [SkippableFact]
    public void Hybrid_search_finds_the_directive_and_rejects_off_topic()
    {
        var db = RepoPaths.Resolve("data/takam.db");
        Skip.IfNot(File.Exists(db) && File.Exists(TestPaths.Model), "Base ou modèle absent : lancer download-model puis 'build'.");
        using var index = SearchIndex.Open(db);
        using var embeddings = new E5OnnxEmbeddingService(TestPaths.Model, TestPaths.SentencePiece);
        var engine = new HybridSearchEngine(index, embeddings, new SearchOptions());
        Skip.IfNot(engine.SemanticAvailable, "Base construite sans embeddings.");

        var report = new Evaluator(index, engine, null).RunEngine(Questions, SearchMode.Hybrid);

        Assert.True(report.DirectiveTop5 >= 19, $"Directive dans le top 5 : {report.DirectiveTop5}/{report.Answerable}");
        Assert.Equal(report.OffTopicIds.Count, report.OffTopicRejected);
    }
}
