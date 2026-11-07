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
