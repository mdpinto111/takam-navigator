using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Takam.Core.Configuration;
using Takam.Core.Embeddings;
using Takam.Core.Evaluation;
using Takam.Core.Model;
using Takam.Core.Parsing;
using Takam.Core.Search;
using Takam.Core.Site;
using Takam.Core.Storage;
using Takam.Ingestion;

// Takam.Ingestion — prepares the search data.
//   fetch            downloads the configured chapter from takam.mof.gov.il into data/raw
//   build [--source sample|raw]   extracts the sections and builds data/takam.db (+ embeddings if the model is present)
//   eval             compares the current site and the search modes on data/eval/questions.json
//   inspect <code>   prints the sections extracted from a directive (e.g. inspect 1.4.3)

Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false).AddEnvironmentVariables("TAKAM_");
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
var api = builder.Configuration.GetSection(TakamApiOptions.SectionName).Get<TakamApiOptions>() ?? new();
var data = builder.Configuration.GetSection(DataOptions.SectionName).Get<DataOptions>() ?? new();
var emb = builder.Configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>() ?? new();
var search = builder.Configuration.GetSection(SearchOptions.SectionName).Get<SearchOptions>() ?? new();
builder.Services.AddHttpClient<TakamApiClient>(c =>
{
    c.BaseAddress = new Uri(api.BaseUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(api.TimeoutSeconds);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("TakamNavigator-POC/1.0");
});
builder.Services.AddSingleton(api);
using var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ingestion");

var command = args.FirstOrDefault()?.ToLowerInvariant();
try
{
    return command switch
    {
        "fetch" => await Fetch(),
        "build" => Build(Arg("--source")),
        "eval" => Eval(),
        "inspect" when args.Length > 1 => Inspect(args[1]),
        _ => Usage()
    };
}
catch (Exception ex)
{
    log.LogError(ex, "Command {Command} failed", command);
    return 1;
}

async Task<int> Fetch()
{
    var raw = RepoPaths.Resolve(data.RawDir);
    var client = host.Services.GetRequiredService<TakamApiClient>();
    try
    {
        var (manifest, all) = await client.FetchChapterAsync(raw, CancellationToken.None);
        manifest.Save(Path.Combine(raw, "manifest.json"));
        var keep = all.Select(d => new { d.Sfid, name = d.Name, description = d.Description, keywords = d.Keywords,
            documentcodetext = d.DocumentCodeText, namecode = d.NameCode, documenttype = d.DocumentType });
        File.WriteAllText(Path.Combine(raw, "site-documents.json"), JsonSerializer.Serialize(keep));
        log.LogInformation("Download complete: {Count} directives in {Dir}", manifest.Directives.Count, raw);
        return 0;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        // The site is down: nothing is overwritten, the versioned sample remains usable.
        log.LogError("Site unavailable ({Error}). Existing data is kept; 'build --source sample' is still possible.", ex.Message);
        return 2;
    }
}

int Build(string? source)
{
    var dir = ChooseSource(source);
    var manifest = Manifest.Load(Path.Combine(dir, "manifest.json"));
    log.LogInformation("Source: {Dir} ({Count} directives, fetched on {Date})", dir, manifest.Directives.Count, manifest.FetchedAt);

    var parser = new DocxSectionParser();
    var sections = new Dictionary<string, IReadOnlyList<Section>>();
    foreach (var d in manifest.Directives)
    {
        var path = Path.Combine(dir, d.File ?? $"docx/{d.Code}.docx");
        try { sections[d.Code] = parser.Parse(d.Code, path); }
        catch (Exception ex) { log.LogWarning("Directive {Code} skipped: {Error}", d.Code, ex.Message); }
    }

    using var embeddings = EmbeddingFactory.TryCreate(emb, log) as IDisposable as E5OnnxEmbeddingService;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    new SearchIndexBuilder(embeddings, m => log.LogInformation("{Message}", m))
        .Build(RepoPaths.Resolve(data.DatabasePath), manifest, sections);
    log.LogInformation("Database written: {Path} in {Seconds:F1}s{Mode}", RepoPaths.Resolve(data.DatabasePath), sw.Elapsed.TotalSeconds,
        embeddings is null ? " (no embeddings: keyword search only)" : "");
    return 0;
}

int Eval()
{
    using var index = SearchIndex.Open(RepoPaths.Resolve(data.DatabasePath));
    using var embeddings = EmbeddingFactory.TryCreate(emb, log) as E5OnnxEmbeddingService;
    var engine = new HybridSearchEngine(index, embeddings, search);
    var sitePath = Path.Combine(ChooseSource(null), "site-documents.json");
    var site = File.Exists(sitePath) ? SiteSearchReplica.Load(sitePath) : null;
    var questions = EvalQuestion.Load(RepoPaths.Resolve(data.EvalQuestionsPath));
    var evaluator = new Evaluator(index, engine, site);

    var reports = new List<EvalReport>();
    if (site is not null)
    {
        reports.Add(evaluator.RunSite(questions, useKeywordsVariant: false));
        reports.Add(evaluator.RunSite(questions, useKeywordsVariant: true));
    }
    reports.Add(evaluator.RunEngine(questions, SearchMode.Keyword));
    if (engine.SemanticAvailable)
    {
        reports.Add(evaluator.RunEngine(questions, SearchMode.Semantic));
        reports.Add(evaluator.RunEngine(questions, SearchMode.Hybrid));
        reports.Add(evaluator.RunEngine(questions, SearchMode.Hybrid, useKeywordsVariant: true));
    }

    Console.WriteLine();
    Console.WriteLine($"Evaluation on {questions.Count} questions ({reports[0].Answerable} with a correct answer + {questions.Count - reports[0].Answerable} off-topic)");
    Console.WriteLine($"{"System",-28} {"Directive top 5",16} {"Exact section",15} {"Off-topic rejected",18}");
    foreach (var r in reports)
    {
        var section = r.System.StartsWith("Current site") ? "—" : $"{r.SectionTop5}/{r.Answerable}";
        Console.WriteLine($"{r.System,-28} {r.DirectiveTop5 + "/" + r.Answerable,16} {section,15} {r.OffTopicRejected + "/" + r.OffTopicIds.Count,18}");
    }
    if (engine.SemanticAvailable)
    {
        // Threshold tuning aid: confidence signals for each question (meaning, word coverage).
        Console.WriteLine();
        Console.WriteLine("Confidence signals (meaning / word coverage):");
        foreach (var q in questions.Where(q => q.Kind != "number"))
        {
            var (sem, cov) = engine.ConfidenceSignals(q.Query);
            Console.WriteLine($"  {q.Id} {(q.ExpectedDirective is null ? "OFF-TOPIC " : "business  ")} meaning={sem:F3} coverage={cov:P0}  {q.Query}");
        }
    }
    var best = reports.Last(r => !r.System.Contains("keywords"));
    Console.WriteLine($"\nDetails — {best.System}:");
    foreach (var o in best.Outcomes)
    {
        var q = questions.First(x => x.Id == o.QuestionId);
        var status = o.DirectiveFound ? (o.SectionFound ? "OK      " : "OK(dir) ") : "FAIL    ";
        Console.WriteLine($"  {o.QuestionId} {status} rang={o.Rank?.ToString() ?? "-",-2} {q.Query}  →  {o.Detail}");
    }
    return 0;
}

int Inspect(string code)
{
    var dir = ChooseSource(null);
    var manifest = Manifest.Load(Path.Combine(dir, "manifest.json"));
    var d = manifest.Directives.FirstOrDefault(x => x.Code == code) ?? throw new ArgumentException($"Directive {code} is not in the manifest.");
    var sections = new DocxSectionParser().Parse(code, Path.Combine(dir, d.File!));
    Console.WriteLine($"{d.Code} – {d.Name} (edition {d.Edition}): {sections.Count} sections");
    foreach (var s in sections)
        Console.WriteLine($"  [{s.Label}] ({s.Heading}) {Shorten(s.Text.Replace('\n', ' '), 110)}");
    return 0;
}

string ChooseSource(string? source)
{
    var raw = RepoPaths.Resolve(data.RawDir);
    var sample = RepoPaths.Resolve(data.SampleDir);
    return source?.ToLowerInvariant() switch
    {
        "raw" => File.Exists(Path.Combine(raw, "manifest.json")) ? raw : throw new FileNotFoundException("No downloaded data: run 'fetch' first."),
        "sample" => sample,
        _ => File.Exists(Path.Combine(raw, "manifest.json")) ? raw : sample
    };
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Shorten(string s, int n) => s.Length <= n ? s : s[..n] + "…";

static int Usage()
{
    Console.WriteLine("""
        Usage: dotnet run --project src/Takam.Ingestion -- <command>
          fetch                        downloads the chapter from takam.mof.gov.il (data/raw)
          build [--source sample|raw]  builds the search database (data/takam.db)
          eval                         compares the current site with the hybrid search (data/eval/questions.json)
          inspect <code>               prints the sections extracted from a directive (e.g. 1.4.3)
        """);
    return 1;
}
