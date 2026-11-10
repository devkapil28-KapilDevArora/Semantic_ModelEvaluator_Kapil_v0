using System.Xml.Linq;
using SemanticModelEvaluator.App.Services;
using SemanticModelEvaluator.App.ViewModels;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Comparison;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers;
using SemanticModelEvaluator.Providers.Onnx;
using Xunit;

namespace SemanticModelEvaluator.Tests;

public sealed class TestDialogs : IUserDialogs
{
    public string? Path { get; set; }
    public string? Open(string filter) => Path;
    public string? Save(string filter, string extension) => Path;
    public string? Folder() => Path;
    public bool Confirm(string message) => true;
}

public class WorkbenchTests
{
    [Fact]
    public async Task P1_two_models_threshold_reference_scenario_save_open_and_export()
    {
        using var root = new TempAppRoot();
        var registry = new ProviderRegistry([new DeterministicProvider()]);
        var dialogs = new TestDialogs();
        using var model = new WorkbenchViewModel(registry, new EvaluationEngine(registry, new EmbeddingCache(AppPaths.EmbeddingCache), new FileRunStore(AppPaths.Runs)),
            new MetricRegistry(), dialogs, new SettingsStore(AppPaths.SettingsFile), new AppLog(), new NetworkPolicy { OfflineMode = true });
        await model.InitializeAsync();
        await model.SampleCommand.ExecuteAsync(null);
        Assert.Equal(2, model.Models.Count(m => m.Selected));
        Assert.NotNull(model.Dataset);
        await model.RunCommand.ExecuteAsync(null);
        Assert.False(model.Busy);
        Assert.Equal(2, model.Scenarios.Count);
        Assert.NotNull(model.MetricGrid);
        Assert.NotNull(model.Inspector);
        var before = model.Workspace.Runs.Single().Models.SelectMany(r => r.Queries).Sum(q => q.Results.Count);
        model.UseThreshold = true; model.Threshold = 0.4;
        await model.RecalculateAsync();
        Assert.Equal(before, model.Workspace.Runs.Single().Models.SelectMany(r => r.Queries).Sum(q => q.Results.Count));
        model.Reference = model.Scenarios[1]; model.ScenarioName = "Saved threshold";
        model.SaveScenarioCommand.Execute(null);
        Assert.Equal(3, model.Scenarios.Count);
        model.Notes = "Round-trip note"; model.Filter = "vpn";
        model.ComparisonTab = 2; model.ScenarioNotes = "Scenario-specific note";
        var savedScenarioId = model.SelectedScenario!.Id;
        dialogs.Path = System.IO.Path.Combine(root.Root, "comparison.smeval");
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.Dirty);
        Assert.True(File.Exists(dialogs.Path));
        await model.OpenCommand.ExecuteAsync(null);
        Assert.Equal("Round-trip note", model.Notes);
        Assert.Equal("vpn", model.Filter);
        Assert.Equal(2, model.ComparisonTab);
        Assert.Equal(savedScenarioId, model.SelectedScenario!.Id);
        Assert.Equal("Scenario-specific note", model.ScenarioNotes);
        Assert.Equal("2", model.Workspace.Runs[0].Config.MetricVersions["precision_at_k"]);
        Assert.Equal(3, model.Scenarios.Count);
        Assert.Equal(model.Scenarios[1].Id, model.Reference!.Id);
        Assert.Contains(model.Scenarios, s => s.Name == "Saved threshold" && s.Threshold == 0.4);
        Assert.NotNull(model.MetricGrid);
        dialogs.Path = System.IO.Path.Combine(root.Root, "report.json");
        await model.ExportCommand.ExecuteAsync(null);
        var report = File.ReadAllText(dialogs.Path);
        Assert.Contains("exampleTextIncluded", report);
        Assert.Contains("deterministic:hash-word-256@1", report);
        Assert.DoesNotContain("I forgot my login password", report);
    }

    [Fact]
    public async Task Command_errors_are_visible_and_busy_state_recovers()
    {
        using var root = new TempAppRoot();
        var providers = new ProviderRegistry([]);
        using var model = new WorkbenchViewModel(providers, new EvaluationEngine(providers), new MetricRegistry(),
            new TestDialogs(), new SettingsStore(AppPaths.SettingsFile), new AppLog(), new NetworkPolicy());
        await model.RunCommand.ExecuteAsync(null);
        Assert.False(model.Busy);
        Assert.StartsWith("Error:", model.Status);
        Assert.Contains("Load a dataset", model.Status);
    }

    [Fact]
    public void Dataset_hub_is_reachable_without_comparison_results()
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SemanticModelEvaluator.App", "MainWindow.xaml"));
        var document = XDocument.Load(path);
        var hub = document.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "DatasetHubTab");
        Assert.Equal("MainNavigation", (string?)hub.Parent?.Attribute("AutomationProperties.AutomationId"));
        Assert.DoesNotContain(hub.Ancestors(), e => (string?)e.Attribute("AutomationProperties.AutomationId") == "PairResults");
        var browse = document.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "PairBrowseDatasets");
        Assert.DoesNotContain(browse.Ancestors(), e => (string?)e.Attribute("AutomationProperties.AutomationId") == "PairResults");
    }

    [Fact]
    public void Required_UI_actions_have_unique_automation_identifiers()
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SemanticModelEvaluator.App", "MainWindow.xaml"));
        var document = XDocument.Load(path);
        var ids = document.Descendants().Attributes().Where(a => a.Name.LocalName == "AutomationProperties.AutomationId").Select(a => a.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var id in new[] { "OpenWorkspace", "SaveWorkspace", "LoadDemo", "RunEvaluation", "CancelEvaluation",
            "ThresholdSlider", "ThresholdValue", "SideBySideResults", "ReferenceScenario", "ComparisonGrid", "SaveScenario", "ExportComparison" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void Average_precision_ties_are_order_independent()
    {
        Assert.Equal(0.5, BuiltInMetrics.AveragePrecision([(true, 0.5), (false, 0.5)]));
        Assert.Equal(BuiltInMetrics.AveragePrecision([(true, 0.5), (false, 0.5)]),
            BuiltInMetrics.AveragePrecision([(false, 0.5), (true, 0.5)]));
    }

    [Theory]
    [InlineData("q1,d1,0.9\nq1,d1,0.8")]
    [InlineData("q1,d1,NaN")]
    [InlineData("q1,d1,Infinity")]
    public void Invalid_predictions_are_rejected(string rows)
    {
        using var root = new TempAppRoot();
        var path = System.IO.Path.Combine(root.Root, "predictions.csv");
        File.WriteAllText(path, "query_id,doc_id,score\n" + rows);
        Assert.Throws<InvalidDataException>(() => PredictionImporter.Import(path, new("test", "x", "1"), "x", Fixtures.Retrieval()));
    }

    [Fact]
    public void Local_model_hash_includes_vocabulary_and_pooling_without_writing_source()
    {
        using var root = new TempAppRoot();
        File.WriteAllBytes(System.IO.Path.Combine(root.Root, "model.onnx"), [1, 2, 3]);
        var vocab = System.IO.Path.Combine(root.Root, "vocab.txt");
        File.WriteAllText(vocab, "[PAD]\n[UNK]\n[CLS]\n[SEP]\na");
        Directory.CreateDirectory(System.IO.Path.Combine(root.Root, "1_Pooling"));
        File.WriteAllText(System.IO.Path.Combine(root.Root, "1_Pooling", "config.json"), """{"pooling_mode_mean_tokens":true}""");
        var first = OnnxModelFolder.Inspect(root.Root).ComputeContentHash();
        File.AppendAllText(vocab, "\nb");
        var second = OnnxModelFolder.Inspect(root.Root).ComputeContentHash();
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(System.IO.Path.Combine(root.Root, "smeval-model.json")));
    }

    [Fact]
    public void Cache_cleanup_preserves_unrelated_files()
    {
        using var root = new TempAppRoot();
        var cache = new EmbeddingCache(System.IO.Path.Combine(root.Root, "cache"));
        cache.Save("model", "Document", new Dictionary<string, float[]> { ["hash"] = [1, 0] });
        var unrelated = System.IO.Path.Combine(cache.Root, "keep.txt");
        File.WriteAllText(unrelated, "not a cache file");
        cache.Clear();
        Assert.Equal("not a cache file", File.ReadAllText(unrelated));
        Assert.Empty(cache.Load("model", "Document"));
    }

    [Fact]
    public void Invalid_thresholds_and_zero_K_are_rejected()
    {
        var engine = new ThresholdEngine(new MetricRegistry());
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Evaluate(Fixtures.Retrieval(), Fixtures.RetrievalRunA(), null, 0, ["precision_at_k"]));
        Assert.Throws<ArgumentException>(() => engine.Evaluate(Fixtures.Retrieval(), Fixtures.RetrievalRunA(), double.NaN, 5, ["precision_at_k"]));
    }
}
