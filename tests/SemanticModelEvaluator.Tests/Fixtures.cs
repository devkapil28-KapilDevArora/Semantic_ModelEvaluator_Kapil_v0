using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Tests;

/// <summary>Small hand-built fixtures whose metric values are computed by hand in the tests.</summary>
public static class Fixtures
{
    public static string SamplesDir => Path.Combine(AppContext.BaseDirectory, "samples");
    public static string HelpdeskManifest => Path.Combine(SamplesDir, "retrieval-it-helpdesk", "it-helpdesk.smds.json");
    public static string SafetyManifest => Path.Combine(SamplesDir, "classification-safety", "safety-requests.smds.json");
    public static string StsManifest => Path.Combine(SamplesDir, "similarity-sts", "sts.smds.json");

    /// <summary>
    /// q1: d1 grade 2 (safe), d2 grade 0 (harmful), d3 grade 1 (safe). q2: d3 grade 1. q3: no judgments.
    /// </summary>
    public static EvalDataset Retrieval() => new()
    {
        Id = "fx-retrieval",
        Name = "Fixture retrieval",
        Task = EvalTask.Retrieval,
        ContentHash = "fx1",
        IsSynthetic = true,
        Queries = [new("q1", "alpha"), new("q2", "beta"), new("q3", "gamma")],
        Documents = [new("d1", "one"), new("d2", "two"), new("d3", "three"), new("d4", "four"), new("d5", "five")],
        Judgments =
        [
            new("q1", "d1", 2, false), new("q1", "d2", 0, true), new("q1", "d3", 1, false),
            new("q2", "d3", 1),
        ]
    };

    /// <summary>q1: d2 .9, d1 .8, d4 .5 · q2: d5 .3 · q3: d1 .7</summary>
    public static ModelRunResult RetrievalRunA() => new()
    {
        Model = new ModelIdentity("test", "model-a", "1"),
        DisplayName = "Model A",
        Status = RunStatus.Completed,
        Queries =
        [
            new() { QueryId = "q1", Results = [new("d2", 0.9), new("d1", 0.8), new("d4", 0.5)], LatencyMs = 10 },
            new() { QueryId = "q2", Results = [new("d5", 0.3)], LatencyMs = 20 },
            new() { QueryId = "q3", Results = [new("d1", 0.7)], LatencyMs = 30 },
        ],
        Performance = new PerformanceStats { MeanLatencyMs = 20, Measured = true }
    };

    /// <summary>Perfect on q1 and q2.</summary>
    public static ModelRunResult RetrievalRunB() => new()
    {
        Model = new ModelIdentity("test", "model-b", "1"),
        DisplayName = "Model B",
        Status = RunStatus.Completed,
        Queries =
        [
            new() { QueryId = "q1", Results = [new("d1", 0.95), new("d3", 0.85), new("d2", 0.1)], LatencyMs = 40 },
            new() { QueryId = "q2", Results = [new("d3", 0.9)], LatencyMs = 40 },
            new() { QueryId = "q3", Results = [], LatencyMs = 40 },
        ],
        Performance = new PerformanceStats { MeanLatencyMs = 40, Measured = true }
    };

    /// <summary>Positive label "unsafe". e5 has no prediction.</summary>
    public static EvalDataset Classification() => new()
    {
        Id = "fx-class",
        Name = "Fixture classification",
        Task = EvalTask.Safety,
        PositiveLabel = "unsafe",
        ContentHash = "fx2",
        Examples =
        [
            new("e1", "a", Label: "unsafe"), new("e2", "b", Label: "unsafe"), new("e3", "c", Label: "safe"),
            new("e4", "d", Label: "safe"), new("e5", "e", Label: "unsafe"), new("e6", "f"),
        ]
    };

    public static ModelRunResult ClassificationRun() => new()
    {
        Model = new ModelIdentity("test", "clf", "1"),
        DisplayName = "Classifier",
        Status = RunStatus.Completed,
        Examples =
        [
            new() { ExampleId = "e1", Score = 0.9 }, new() { ExampleId = "e2", Score = 0.4 },
            new() { ExampleId = "e3", Score = 0.6 }, new() { ExampleId = "e4", Score = 0.1 },
            new() { ExampleId = "e6", Score = 0.99 },
        ]
    };

    public static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "smeval-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>Redirects AppPaths to a private temp root for the lifetime of a test.</summary>
public sealed class TempAppRoot : IDisposable
{
    public string Root { get; } = Fixtures.NewTempDir();
    public TempAppRoot() => AppPaths.SetRoot(Root);
    public void Dispose()
    {
        AppPaths.SetRoot(null);
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
