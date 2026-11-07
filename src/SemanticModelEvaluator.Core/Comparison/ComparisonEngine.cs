using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Comparison;

/// <summary>A saved configuration: one model's results from one run under a given threshold and K.</summary>
public sealed class Scenario
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string RunId { get; set; } = "";
    public string ModelKey { get; set; } = "";
    public double? Threshold { get; set; }
    public int K { get; set; } = 10;
    public string Color { get; set; } = "#0F6CBD";
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public Scenario Copy() => (Scenario)MemberwiseClone();
}

public sealed record ScenarioEvaluation(Scenario Scenario, ModelRunResult Result, MetricSnapshot Snapshot);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DeltaAssessment>))]
public enum DeltaAssessment { Reference, Better, Worse, Same, NoDirection, NotComparable }

public sealed record ComparisonCell(string ScenarioId, double? Value, double? Delta, DeltaAssessment Assessment, int SampleCount, int JudgedCount, int MissingCount, string? Note);

public sealed record ComparisonRow(MetricDefinition Metric, IReadOnlyList<ComparisonCell> Cells);

public sealed class ComparisonTable
{
    public required string ReferenceScenarioId { get; init; }
    public required IReadOnlyList<Scenario> Scenarios { get; init; }
    public required IReadOnlyList<ComparisonRow> Rows { get; init; }
}

public static class ComparisonEngine
{
    public const double SameTolerance = 1e-9;

    /// <summary>delta = scenario − reference. Assessment uses the metric's declared direction; Neutral metrics are never called better/worse.</summary>
    public static ComparisonTable Compare(IReadOnlyList<ScenarioEvaluation> evaluations, string referenceScenarioId, IEnumerable<MetricDefinition> metrics)
    {
        if (evaluations.Count == 0) throw new ArgumentException("At least one scenario is required.");
        var reference = evaluations.FirstOrDefault(e => e.Scenario.Id == referenceScenarioId) ?? evaluations[0];
        var rows = new List<ComparisonRow>();
        foreach (var m in metrics)
        {
            var refValue = reference.Snapshot.Values.GetValueOrDefault(m.Id)?.Value;
            var cells = evaluations.Select(e =>
            {
                var mv = e.Snapshot.Values.GetValueOrDefault(m.Id);
                var v = mv?.Value;
                if (e == reference) return new ComparisonCell(e.Scenario.Id, v, v is null ? null : 0, DeltaAssessment.Reference, mv?.SampleCount ?? 0, mv?.JudgedCount ?? 0, mv?.MissingCount ?? 0, mv?.Note);
                if (v is null || refValue is null) return new ComparisonCell(e.Scenario.Id, v, null, DeltaAssessment.NotComparable, mv?.SampleCount ?? 0, mv?.JudgedCount ?? 0, mv?.MissingCount ?? 0, mv?.Note);
                double d = v.Value - refValue.Value;
                var a = m.Direction switch
                {
                    _ when Math.Abs(d) <= SameTolerance => DeltaAssessment.Same,
                    Direction.HigherIsBetter => d > 0 ? DeltaAssessment.Better : DeltaAssessment.Worse,
                    Direction.LowerIsBetter => d < 0 ? DeltaAssessment.Better : DeltaAssessment.Worse,
                    _ => DeltaAssessment.NoDirection
                };
                return new ComparisonCell(e.Scenario.Id, v, d, a, mv!.SampleCount, mv.JudgedCount, mv.MissingCount, mv.Note);
            }).ToList();
            rows.Add(new ComparisonRow(m, cells));
        }
        return new ComparisonTable { ReferenceScenarioId = reference.Scenario.Id, Scenarios = [.. evaluations.Select(e => e.Scenario)], Rows = rows };
    }
}

public sealed record RadarAxis(string MetricId, string Label, string Transformation);
public sealed record RadarSeries(string ScenarioId, string Name, string Color, double?[] Values);
public sealed record RadarProjection(IReadOnlyList<RadarAxis> Axes, IReadOnlyList<RadarSeries> Series, IReadOnlyList<string> Excluded);

public static class RadarProjector
{
    /// <summary>
    /// Only metrics with a fixed [0,1] range and a declared direction are drawn; lower-is-better metrics are shown as 1 − value.
    /// No min-max normalisation across models is applied, so the chart cannot exaggerate small differences.
    /// </summary>
    public static RadarProjection Project(IReadOnlyList<ScenarioEvaluation> evaluations, IEnumerable<MetricDefinition> metrics)
    {
        var axes = new List<RadarAxis>();
        var excluded = new List<string>();
        foreach (var m in metrics)
        {
            if (!m.IsBounded01) { excluded.Add($"{m.Name}: not on a fixed 0–1 scale"); continue; }
            if (m.Direction == Direction.Neutral) { excluded.Add($"{m.Name}: no preferred direction"); continue; }
            axes.Add(m.Direction == Direction.LowerIsBetter
                ? new RadarAxis(m.Id, "1 − " + m.Name, "Shown as 1 − value so outward is better")
                : new RadarAxis(m.Id, m.Name, "Raw value"));
        }
        var series = evaluations.Select(e => new RadarSeries(e.Scenario.Id, e.Scenario.Name, e.Scenario.Color,
            [.. axes.Select(a =>
            {
                var v = e.Snapshot[a.MetricId];
                return v is null ? (double?)null : a.Label.StartsWith("1 − ") ? 1 - v.Value : v.Value;
            })])).ToList();
        return new RadarProjection(axes, series, excluded);
    }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<FailureKind>))]
public enum FailureKind { MissedRelevant, EmptyResult, HarmfulRetained, FalsePositive, FalseNegative, Misclassified, MissingPrediction }

public sealed record ItemFailure(string ItemId, FailureKind Kind, IReadOnlyList<string> ScenarioIds);

public sealed class ErrorAnalysisResult
{
    public List<ItemFailure> Shared { get; } = [];
    public Dictionary<string, List<ItemFailure>> ModelSpecific { get; } = [];
    public List<ItemFailure> All { get; } = [];
    public List<string> Notes { get; } = [];
}

public static class ErrorAnalysis
{
    /// <summary>Only computes categories the dataset's labels support (e.g. MissedRelevant needs relevance judgments).</summary>
    public static ErrorAnalysisResult Analyze(EvalDataset dataset, IReadOnlyList<ScenarioEvaluation> evaluations)
    {
        var result = new ErrorAnalysisResult();
        var failures = new Dictionary<(string Item, FailureKind Kind), List<string>>();
        void Add(string item, FailureKind kind, string scenario)
        {
            if (!failures.TryGetValue((item, kind), out var l)) failures[(item, kind)] = l = [];
            l.Add(scenario);
        }

        bool retrieval = dataset.Task is EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking;
        foreach (var e in evaluations)
        {
            var ctx = new MetricContext { Dataset = dataset, Result = e.Result, Threshold = e.Scenario.Threshold, K = e.Scenario.K };
            if (retrieval)
            {
                foreach (var q in ctx.RetainedQueries)
                {
                    if (q.Failed) { Add(q.QueryId, FailureKind.MissingPrediction, e.Scenario.Id); continue; }
                    if (q.Retained.Count == 0) Add(q.QueryId, FailureKind.EmptyResult, e.Scenario.Id);
                    if (dataset.JudgmentIndex.RelevantCount(q.QueryId) > 0 && !q.Retained.Any(d => dataset.JudgmentIndex.Get(q.QueryId, d.DocumentId)?.Grade > 0))
                        Add(q.QueryId, FailureKind.MissedRelevant, e.Scenario.Id);
                    if (dataset.HasSafetyJudgments && q.Retained.Any(d => dataset.JudgmentIndex.Get(q.QueryId, d.DocumentId)?.Harmful == true))
                        Add(q.QueryId, FailureKind.HarmfulRetained, e.Scenario.Id);
                }
            }
            else if (dataset.Task is EvalTask.Classification or EvalTask.Safety)
            {
                var predicted = ctx.ClassifiedExamples().ToDictionary(x => x.Example.Id);
                foreach (var ex in dataset.Examples.Where(x => x.Label is not null))
                {
                    if (!predicted.TryGetValue(ex.Id, out var p)) { Add(ex.Id, FailureKind.MissingPrediction, e.Scenario.Id); continue; }
                    if (p.Predicted == ex.Label) continue;
                    var pos = dataset.PositiveLabel;
                    if (pos is null) Add(ex.Id, FailureKind.Misclassified, e.Scenario.Id);
                    else Add(ex.Id, p.Predicted == pos ? FailureKind.FalsePositive : FailureKind.FalseNegative, e.Scenario.Id);
                }
            }
        }
        if (retrieval && !dataset.HasRelevanceJudgments) result.Notes.Add("No relevance judgments: missed-relevant failures cannot be identified.");
        if (retrieval && !dataset.HasSafetyJudgments) result.Notes.Add("No safety judgments: harmful retrievals cannot be identified.");
        if (dataset.Task is EvalTask.SemanticSimilarity or EvalTask.Generative) result.Notes.Add("Failure categories are not defined for this task; use the qualitative view.");

        foreach (var ((item, kind), scenarios) in failures.OrderBy(f => f.Key.Item, StringComparer.Ordinal))
        {
            var f = new ItemFailure(item, kind, scenarios);
            result.All.Add(f);
            if (evaluations.Count > 1 && scenarios.Count == evaluations.Count) result.Shared.Add(f);
            else if (scenarios.Count == 1)
            {
                if (!result.ModelSpecific.TryGetValue(scenarios[0], out var l)) result.ModelSpecific[scenarios[0]] = l = [];
                l.Add(f);
            }
        }
        return result;
    }
}
