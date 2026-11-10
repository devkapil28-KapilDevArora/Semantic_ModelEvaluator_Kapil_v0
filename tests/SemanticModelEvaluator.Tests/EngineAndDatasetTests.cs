using System.Text;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Providers;
using Xunit;

namespace SemanticModelEvaluator.Tests;

/// <summary>Test provider whose runners fail, hang, or work on demand.</summary>
public sealed class FakeProvider(string id, Func<string, IModelRunner> make) : IModelProvider
{
    public string Id => id;
    public string DisplayName => id;
    public bool UsesNetwork => false;
    public string PrivacyNotice => "";
    public Task<ProviderHealth> CheckHealthAsync(CancellationToken c) => Task.FromResult(ProviderHealth.Ok());
    public Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery q, CancellationToken c) => Task.FromResult<IReadOnlyList<ModelDescriptor>>([]);
    public Task<ValidationReport> ValidateAsync(ModelDescriptor m, CancellationToken c) => Task.FromResult(new ValidationReport());
    public Task<IModelRunner> LoadAsync(ModelDescriptor m, RuntimeOptions o, CancellationToken c) => Task.FromResult<IModelRunner>(make(m.Identity.ModelId));

    public static ModelDescriptor Model(string providerId, string id) => new()
    {
        Identity = new ModelIdentity(providerId, id, "1"),
        DisplayName = id,
        Tasks = [EvalTask.Retrieval, EvalTask.SemanticSimilarity],
        Readiness = ReadinessState.Ready,
        Execution = ExecutionKind.Deterministic
    };
}

public sealed class LambdaRunner(ModelDescriptor model, Func<IReadOnlyList<string>, EmbeddingRole, CancellationToken, Task<float[][]>> f) : ITextEmbeddingRunner
{
    public ModelDescriptor Model { get; } = model;
    public RuntimeInfo Runtime { get; } = new("test", "CPU", 1);
    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct) => f(texts, role, ct);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class EvaluationEngineTests
{
    private static ProviderRegistry Registry(params IModelProvider[] extra) => new([new DeterministicProvider(), .. extra]);

    private sealed class DatasetRunner(ModelDescriptor model, ProgressInfo update) : IDatasetModelRunner
    {
        public ModelDescriptor Model { get; } = model;
        public RuntimeInfo Runtime { get; } = new("test", "CPU", 1);
        public bool Disposed { get; private set; }

        public Task EvaluateAsync(EvalDataset dataset, EvaluationConfig config, ModelRunResult result,
            IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(update);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData(0.0, 0, 100)]
    [InlineData(0.42, 42, 100)]
    [InlineData(1.0, 100, 100)]
    public async Task Dataset_runner_progress_uses_message_and_fraction_without_reporting_a_warning(double? fraction, int completed, int total)
    {
        var model = FakeProvider.Model("dataset", "whole-dataset");
        model.Execution = ExecutionKind.ExternalProcess;
        model.OutputModality = Modality.Scores;
        model.Properties["dataset_execution"] = "true";
        var runner = new DatasetRunner(model, new ProgressInfo("Evaluating dataset", fraction));
        var engine = new EvaluationEngine(Registry(new FakeProvider("dataset", _ => runner)));
        var progress = new List<EvaluationProgress>();

        var run = await engine.RunAsync(Fixtures.Retrieval(), [new(model)], new EvaluationConfig(),
            new SyncProgress<EvaluationProgress>(progress.Add), CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        var update = Assert.Single(progress, p => p.Stage == "Evaluating dataset");
        Assert.Equal(completed, update.Completed);
        Assert.Equal(total, update.Total);
        Assert.Equal(total - completed, update.Remaining);
        Assert.Equal(model.DisplayName, update.ModelName);
        Assert.Null(update.Warning);
        Assert.True(runner.Disposed);
    }

    [Fact]
    public async Task Runs_two_deterministic_models_and_imported_predictions_on_sample()
    {
        using var root = new TempAppRoot();
        var ds = DatasetLoader.LoadManifest(Fixtures.HelpdeskManifest);
        Assert.Equal(12, ds.Queries.Count);
        Assert.Equal(40, ds.Documents.Count);
        Assert.True(ds.IsSynthetic);
        var bm25 = ImportedPredictionsProvider.Describe("BM25 (imported)", Path.Combine(Fixtures.SamplesDir, "retrieval-it-helpdesk", "predictions-bm25.csv"), EvalTask.Retrieval);
        var plans = new List<ModelPlan>
        {
            new(DeterministicProvider.Describe("hash-word-256")),
            new(DeterministicProvider.Describe("hash-char3-256")),
            new(bm25),
        };
        var engine = new EvaluationEngine(Registry(new ImportedPredictionsProvider(new Core.Persistence.SettingsStore(Path.Combine(root.Root, "s.json")))),
            new EmbeddingCache(AppPaths.EmbeddingCache), new FileRunStore(AppPaths.Runs));
        var progress = new List<EvaluationProgress>();
        var run = await engine.RunAsync(ds, plans, new EvaluationConfig { K = 5 }, new SyncProgress<EvaluationProgress>(progress.Add), CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.All(run.Models, m => Assert.Equal(RunStatus.Completed, m.Status));
        Assert.All(run.Models, m => Assert.Equal(12, m.Queries.Count));
        Assert.True(run.Models[0].IsSynthetic);
        Assert.NotEmpty(progress);
        Assert.True(File.Exists(Path.Combine(AppPaths.Runs, run.RunId + ".json")));

        var te = new Core.Metrics.ThresholdEngine(new Core.Metrics.MetricRegistry());
        foreach (var m in run.Models)
        {
            var s = te.Evaluate(ds, m, null, 5, ["ndcg_at_k", "harm_rate_at_k"]);
            Assert.InRange(s["ndcg_at_k"]!.Value, 0, 1);
        }
        // Deterministic: a second run gives identical scores (also exercising the embedding cache).
        var run2 = await engine.RunAsync(ds, plans.Take(1).ToList(), new EvaluationConfig { K = 5 }, null, CancellationToken.None);
        Assert.Equal(run.Models[0].Queries.SelectMany(q => q.Results).Select(r => r.Score), run2.Models[0].Queries.SelectMany(q => q.Results).Select(r => r.Score));
    }

    [Fact]
    public async Task One_failing_model_does_not_discard_others()
    {
        using var root = new TempAppRoot();
        var failing = new FakeProvider("fail", id => new LambdaRunner(FakeProvider.Model("fail", id), (_, _, _) => throw new InvalidOperationException("boom")));
        var engine = new EvaluationEngine(Registry(failing), null, new FileRunStore(AppPaths.Runs));
        var ds = Fixtures.Retrieval();
        var run = await engine.RunAsync(ds, [new(DeterministicProvider.Describe("hash-word-256")), new(FakeProvider.Model("fail", "x"))], new EvaluationConfig(), null, CancellationToken.None);
        Assert.Equal(RunStatus.CompletedWithErrors, run.Status);
        Assert.Equal(RunStatus.Completed, run.Models[0].Status);
        Assert.Equal(3, run.Models[0].Queries.Count);
        Assert.Equal(RunStatus.Failed, run.Models[1].Status);
        Assert.Contains("boom", run.Models[1].Error);
    }

    [Fact]
    public async Task Missing_provider_fails_only_that_model()
    {
        using var root = new TempAppRoot();
        var engine = new EvaluationEngine(Registry(), null, null);
        var ghost = FakeProvider.Model("not-installed", "ghost");
        var run = await engine.RunAsync(Fixtures.Retrieval(), [new(ghost), new(DeterministicProvider.Describe("hash-word-256"))], new EvaluationConfig(), null, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, run.Models[0].Status);
        Assert.Contains("not-installed", run.Models[0].Error);
        Assert.Equal(RunStatus.Completed, run.Models[1].Status);
    }

    [Fact]
    public async Task Cancellation_stops_run_and_keeps_completed_models()
    {
        using var root = new TempAppRoot();
        using var cts = new CancellationTokenSource();
        var slow = new FakeProvider("slow", id => new LambdaRunner(FakeProvider.Model("slow", id), async (t, _, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }));
        var store = new FileRunStore(AppPaths.Runs);
        var engine = new EvaluationEngine(Registry(slow), null, store);
        var run = await engine.RunAsync(Fixtures.Retrieval(), [new(DeterministicProvider.Describe("hash-word-256")), new(FakeProvider.Model("slow", "s"))], new EvaluationConfig(), null, cts.Token);
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal(RunStatus.Completed, run.Models[0].Status);
        Assert.Equal(RunStatus.Cancelled, run.Models[1].Status);
        Assert.True(File.Exists(Path.Combine(AppPaths.Runs, run.RunId + ".json")));
    }

    [Fact]
    public async Task Similarity_task_scores_pairs()
    {
        using var root = new TempAppRoot();
        var ds = DatasetLoader.LoadManifest(Fixtures.StsManifest);
        var engine = new EvaluationEngine(Registry(), null, null);
        var run = await engine.RunAsync(ds, [new(DeterministicProvider.Describe("hash-word-256"))], new EvaluationConfig(), null, CancellationToken.None);
        Assert.Equal(20, run.Models[0].Examples.Count);
        var s = new Core.Metrics.ThresholdEngine(new Core.Metrics.MetricRegistry()).Evaluate(ds, run.Models[0], null, 10, ["spearman"]);
        Assert.InRange(s["spearman"]!.Value, -1, 1);
    }

    [Fact]
    public async Task Imported_classifier_predictions_produce_safety_metrics_and_flag_missing()
    {
        using var root = new TempAppRoot();
        var ds = DatasetLoader.LoadManifest(Fixtures.SafetyManifest);
        Assert.Equal("unsafe", ds.PositiveLabel);
        var dir = Path.Combine(Fixtures.SamplesDir, "classification-safety");
        var a = ImportedPredictionsProvider.Describe("Classifier A", Path.Combine(dir, "predictions-classifier-a.csv"), EvalTask.Safety);
        var b = ImportedPredictionsProvider.Describe("Classifier B", Path.Combine(dir, "predictions-classifier-b.csv"), EvalTask.Safety);
        var engine = new EvaluationEngine(Registry(), null, null);
        var run = await engine.RunAsync(ds, [new(a), new(b)], new EvaluationConfig(), null, CancellationToken.None);
        var te = new Core.Metrics.ThresholdEngine(new Core.Metrics.MetricRegistry());
        var sa = te.Evaluate(ds, run.Models[0], 0.5, 10, ["f1", "roc_auc", "error_rate"]);
        var sb = te.Evaluate(ds, run.Models[1], 0.5, 10, ["f1", "roc_auc", "error_rate"]);
        Assert.Equal(0, sa["error_rate"]!.Value, 6);
        Assert.Equal(1.0 / 30, sb["error_rate"]!.Value, 6);
        Assert.Equal(1, sb.Values["f1"].MissingCount);
    }

    [Fact]
    public void Compatibility_rejects_task_mismatch_and_unready_models()
    {
        var ds = Fixtures.Retrieval();
        var unready = FakeProvider.Model("x", "u");
        unready.Readiness = ReadinessState.NeedsDownload;
        var wrongTask = FakeProvider.Model("x", "w");
        wrongTask.Tasks = [EvalTask.Classification];
        var report = EvaluationEngine.CheckCompatibility([new(unready), new(wrongTask)], ds, EvalTask.Retrieval);
        Assert.False(report.CanRun);
        Assert.Contains(report.Items, i => i.Model == "u" && i.Severity == ValidationSeverity.Error);
        Assert.Contains(report.Items, i => i.Model == "w" && i.Severity == ValidationSeverity.Error);

        var ok = EvaluationEngine.CheckCompatibility([new(DeterministicProvider.Describe("hash-word-256")), new(unready)], ds, EvalTask.Retrieval);
        Assert.True(ok.CanRun);
        Assert.Contains(ok.Items, i => i.Severity == ValidationSeverity.Warning && i.Message.Contains("ynthetic"));
    }

    [Fact]
    public void Topk_breaks_ties_deterministically()
    {
        float[] q = [1, 0];
        float[][] m = [[1, 0], [1, 0], [0, 1]];
        var top = EvaluationEngine.TopK(q, m, ["b", "a", "c"], 2);
        Assert.Equal(["a", "b"], top.Select(t => t.DocumentId));
    }
}

public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public class DatasetTests
{
    [Fact]
    public void Csv_reader_handles_quotes_and_embedded_newlines()
    {
        var dir = Fixtures.NewTempDir();
        var p = Path.Combine(dir, "t.csv");
        File.WriteAllText(p, "id,text\n1,\"hello, \"\"world\"\"\nline2\"\n2,plain\n", Encoding.UTF8);
        var t = TabularReader.Read(p);
        Assert.Equal(["id", "text"], t.Columns);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("hello, \"world\"\nline2", t.Rows[0][t.IndexOf("text")]);
    }

    [Fact]
    public void Jsonl_and_json_array_are_read()
    {
        var dir = Fixtures.NewTempDir();
        var a = Path.Combine(dir, "a.jsonl");
        File.WriteAllText(a, "{\"id\":\"1\",\"grade\":2}\n{\"id\":\"2\",\"grade\":0}\n");
        var b = Path.Combine(dir, "b.json");
        File.WriteAllText(b, "{\"data\":[{\"id\":\"x\",\"text\":\"t\"}]}");
        Assert.Equal(2, TabularReader.Read(a).Rows.Count);
        var table = TabularReader.Read(b);
        Assert.Equal("t", table.Rows[0][table.IndexOf("text")]);
    }

    [Fact]
    public void Mapping_suggestion_flags_ambiguity_and_missing_roles_instead_of_guessing()
    {
        var s = MappingRules.Suggest(TableKind.Queries, ["id", "query", "text"]);
        Assert.NotEmpty(s.Ambiguities);   // both "query" and "text" could be Text
        Assert.DoesNotContain(ColumnRole.Text, s.Columns.Values);

        var m = MappingRules.Suggest(TableKind.Judgments, ["qid", "pid", "rel"]);
        Assert.Contains("DocumentId", m.MissingRequired);
        Assert.Equal(ColumnRole.Ignore, m.Columns["pid"]);
    }

    [Fact]
    public void Validation_rejects_duplicate_roles()
    {
        var r = MappingRules.Validate(TableKind.Queries, new Dictionary<string, ColumnRole> { ["a"] = ColumnRole.Id, ["b"] = ColumnRole.Text, ["c"] = ColumnRole.Text });
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Builder_refuses_unconfirmed_mappings()
    {
        var dir = Fixtures.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "q.csv"), "id,text\n1,a\n");
        var manifest = new DatasetManifest { Name = "x", Tables = [new() { Kind = TableKind.Queries, Path = "q.csv", Columns = { ["id"] = ColumnRole.Id, ["text"] = ColumnRole.Text } }] };
        var path = Path.Combine(dir, "x" + AppPaths.DatasetManifestExtension);
        manifest.Save(path);
        Assert.ThrowsAny<Exception>(() => DatasetLoader.LoadManifest(path));
    }

    [Fact]
    public void Validator_reports_orphan_judgments_and_coverage()
    {
        var ds = DatasetLoader.LoadManifest(Fixtures.HelpdeskManifest);
        Assert.True(ds.HasRelevanceJudgments);
        Assert.True(ds.HasSafetyJudgments);
        Assert.Equal(11.0 / 12, ds.JudgmentCoverage, 6);   // q12 deliberately has no judgments
        Assert.Contains(ds.ValidationIssues, i => i.Message.Contains("judg", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(ds.ContentHash);

        var dir = Fixtures.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "q.csv"), "id,text\nq1,a\nq1,b\n");
        File.WriteAllText(Path.Combine(dir, "d.csv"), "id,text\nd1,a\n");
        File.WriteAllText(Path.Combine(dir, "j.csv"), "query_id,doc_id,grade\nq1,d9,1\n");
        var m = new DatasetManifest
        {
            Name = "bad",
            Tables =
            [
                new() { Kind = TableKind.Queries, Path = "q.csv", Columns = { ["id"] = ColumnRole.Id, ["text"] = ColumnRole.Text }, Confirmed = true },
                new() { Kind = TableKind.Documents, Path = "d.csv", Columns = { ["id"] = ColumnRole.Id, ["text"] = ColumnRole.Text }, Confirmed = true },
                new() { Kind = TableKind.Judgments, Path = "j.csv", Columns = { ["query_id"] = ColumnRole.QueryId, ["doc_id"] = ColumnRole.DocumentId, ["grade"] = ColumnRole.RelevanceGrade }, Confirmed = true },
            ]
        };
        var p = Path.Combine(dir, "bad.smds.json");
        m.Save(p);
        var bad = DatasetLoader.LoadManifest(p);
        Assert.Contains(bad.ValidationIssues, i => i.Message.Contains("uplicate"));
        Assert.Contains(bad.ValidationIssues, i => i.Message.Contains("d9") || i.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Loading_never_modifies_source_files()
    {
        var before = Directory.GetFiles(Path.GetDirectoryName(Fixtures.HelpdeskManifest)!).ToDictionary(f => f, File.ReadAllBytes);
        DatasetLoader.LoadManifest(Fixtures.HelpdeskManifest);
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(f));
    }

    [Fact]
    public void Prediction_import_warns_on_unknown_ids()
    {
        var dir = Fixtures.NewTempDir();
        var p = Path.Combine(dir, "p.csv");
        File.WriteAllText(p, "query_id,doc_id,score\nq1,d1,0.9\nq1,zzz,0.5\nq9,d1,0.1\n");
        var r = PredictionImporter.Import(p, new("imported", "x", "1"), "x", Fixtures.Retrieval());
        Assert.Contains(r.Warnings, w => w.Contains("q9"));
        Assert.Contains(r.Warnings, w => w.Contains("zzz") || w.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("d1", r.Queries.Single(q => q.QueryId == "q1").Results[0].DocumentId);
    }
}
