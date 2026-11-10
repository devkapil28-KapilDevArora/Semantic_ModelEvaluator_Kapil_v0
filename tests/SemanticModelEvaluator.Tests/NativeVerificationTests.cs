using SemanticModelEvaluator.Core.Comparison;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Export;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers;
using SemanticModelEvaluator.Providers.EvalKit;
using Xunit;

namespace SemanticModelEvaluator.Tests;

public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SMEVAL_VERIFY_NETWORK") != "1")
            Skip = "Opt-in network verification: set SMEVAL_VERIFY_NETWORK=1. Downloads two public models; no datasets are transmitted.";
    }
}

public sealed class PsapiFactAttribute : FactAttribute
{
    public PsapiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SMEVAL_VERIFY_PSAPI") != "1")
            Skip = "Opt-in PSAPI verification: set SMEVAL_VERIFY_PSAPI=1 on a machine with a built EvalKit CLI and EvalKit.Backend.Psapi (local CPU, no network).";
    }
}

public class PsapiLiveVerificationTests
{
    [PsapiFact]
    public async Task Space_v6_runs_through_the_evalkit_backend_and_reports_its_identity()
    {
        using var root = new TempAppRoot();
        var cli = new EvalKitCli(new SettingsStore(Path.Combine(root.Root, "settings.json")));
        var status = PsapiBackendStatus.Probe(cli);
        Assert.True(status.CanRun, status.Summary);
        var spec = PsapiProvider.Find("spacev6")!;
        var manifest = EvalKitFixtures.WriteDataset(Path.Combine(root.Root, "ds"));
        var judgements = Path.Combine(root.Root, "ds", "judgements.jsonl");
        File.WriteAllText(judgements, File.ReadAllText(judgements).Replace("\"document_id\"", "\"doc_id\""));
        var ds = Core.EvalKit.EvalKitFormat.LoadDataset(manifest, "Synthetic test data");
        Assert.Empty(PsapiJob.CheckDataset(spec, ds));
        var name = PsapiJob.RunName(spec, ds.Name, DateTimeOffset.Now);
        var job = Path.Combine(root.Root, name + ".job.json");
        File.WriteAllText(job, PsapiJob.Build(spec, manifest, status.PackageDirectory!, 3, name, PsapiJob.TextSource(spec, ds)).ToJsonString());
        var output = Path.Combine(root.Root, "out");
        await cli.RunJobAsync(job, output, null, CancellationToken.None);
        var run = Core.EvalKit.EvalKitFormat.ReadRun(Assert.Single(Core.EvalKit.EvalKitFormat.FindMetricsFiles(output)));
        Assert.Equal("spacev6", run.BackendModelId);
        Assert.Equal(100, run.BackendDimension);
        var results = Core.EvalKit.EvalKitFormat.ReadResults(run);
        Assert.All(results.Queries.SelectMany(q => q.Results), r => Assert.InRange(r.Score, 0, 1));
    }
}

public class NativeVerificationTests
{
    [NetworkFact]
    public async Task Two_public_ONNX_models_execute_offline_and_restore_exact_results()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "native-verification"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var network = new NetworkPolicy();
        var provider = new HuggingFaceProvider(http, network, new InMemorySecretStore(),
            new SettingsStore(Path.Combine(root, "settings.json")), Path.Combine(root, "models"));
        var models = new List<ModelDescriptor>();
        foreach (var id in new[] { "sentence-transformers/all-MiniLM-L6-v2", "BAAI/bge-small-en-v1.5" })
        {
            var info = await provider.GetInfoAsync(id, null, CancellationToken.None);
            var model = await provider.AcquireAsync(provider.Describe(info), null, CancellationToken.None);
            var validation = await provider.ValidateAsync(model, CancellationToken.None);
            Assert.True(validation.IsValid, validation.ToString());
            models.Add(model);
        }
        network.OfflineMode = true;
        var ds = DatasetLoader.LoadManifest(Fixtures.HelpdeskManifest);
        var engine = new EvaluationEngine(new ProviderRegistry([provider]), new EmbeddingCache(Path.Combine(root, "embeddings")), new FileRunStore(Path.Combine(root, "runs")));
        var run = await engine.RunAsync(ds, models.Select(m => new ModelPlan(m)).ToArray(), new EvaluationConfig { K = 5, UseEmbeddingCache = false }, null, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.All(run.Models, r => Assert.Equal(12, r.Queries.Count));
        Assert.All(run.Models, r => Assert.False(r.IsSynthetic));
        var registry = new MetricRegistry();
        var te = new ThresholdEngine(registry);
        string[] ids = ["precision_at_k", "ndcg_at_k", "recall_at_k", "mrr", "judged_at_k"];
        var evaluations = run.Models.Select(r =>
        {
            var snapshot = te.Evaluate(ds, r, null, 5, ids);
            Assert.InRange(snapshot["ndcg_at_k"]!.Value, 0, 1);
            Assert.True(r.Queries.SelectMany(q => q.Results).Select(r => r.Score).Distinct().Count() > 5);
            return new ScenarioEvaluation(new Scenario { Name = r.DisplayName, ModelKey = r.Model.Key, RunId = run.RunId, K = 5 }, r, snapshot);
        }).ToList();
        var workspace = new Workspace { Name = "Two real ONNX models on synthetic helpdesk data", Models = models, Dataset = ds.ToReference(), Runs = [run],
            Scenarios = evaluations.Select(e => e.Scenario).ToList(), ReferenceScenarioId = evaluations[0].Scenario.Id };
        var path = Path.Combine(root, "native-comparison.smeval");
        WorkspaceService.Save(workspace, path);
        var restored = WorkspaceService.Open(path, id => models.FirstOrDefault(m => m.Identity == id));
        Assert.Empty(restored.Warnings);
        Assert.Equal(run.Models[0].Queries[0].Results, restored.Workspace.Runs[0].Models[0].Queries[0].Results);
        var context = new ExportContext { Workspace = workspace, Dataset = ds, Evaluations = evaluations,
            Comparison = ComparisonEngine.Compare(evaluations, evaluations[0].Scenario.Id, ids.Select(id => registry.Find(id)!.Definition)) };
        File.WriteAllText(Path.Combine(root, "verification-summary.json"), ReportExporter.ToJson(context, new()));
    }
}
