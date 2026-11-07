using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Metrics;

public sealed class MetricRegistry
{
    private readonly List<IMetric> metrics;

    public MetricRegistry(IEnumerable<IMetric>? metrics = null) => this.metrics = [.. metrics ?? BuiltInMetrics.Create()];

    public IReadOnlyList<IMetric> All => metrics;

    public void Register(IMetric metric)
    {
        if (metrics.Any(m => m.Definition.Id == metric.Definition.Id)) throw new InvalidOperationException($"Metric '{metric.Definition.Id}' already registered.");
        metrics.Add(metric);
    }

    public IMetric? Find(string id) => metrics.FirstOrDefault(m => m.Definition.Id == id);

    public static MetricInput AvailableInputs(EvalDataset dataset, bool measuredPerformance = true, bool scores = true)
    {
        var inputs = MetricInput.None;
        if (dataset.Queries.Count > 0) inputs |= MetricInput.RankedResults;
        if (dataset.HasRelevanceJudgments) inputs |= MetricInput.RelevanceJudgments;
        if (dataset.HasSafetyJudgments) inputs |= MetricInput.SafetyJudgments;
        if (dataset.HasLabels) inputs |= MetricInput.Labels;
        if (dataset.HasGoldScores) inputs |= MetricInput.GoldScores;
        if (scores) inputs |= MetricInput.Scores;
        if (measuredPerformance) inputs |= MetricInput.MeasuredPerformance;
        return inputs;
    }

    /// <summary>Metrics valid for the task and computable from the inputs actually present.</summary>
    public IReadOnlyList<MetricDefinition> Compatible(EvalTask task, EvalDataset dataset) =>
        [.. metrics.Select(m => m.Definition).Where(d => d.Tasks.Contains(task) && (AvailableInputs(dataset) & d.RequiredInputs) == d.RequiredInputs)];

    public IReadOnlyList<(MetricDefinition Metric, string Reason)> Incompatible(EvalTask task, EvalDataset dataset)
    {
        var have = AvailableInputs(dataset);
        return [.. metrics.Select(m => m.Definition).Where(d => d.Tasks.Contains(task) && (have & d.RequiredInputs) != d.RequiredInputs)
            .Select(d => (d, "Missing: " + (d.RequiredInputs & ~have)))];
    }
}

public sealed class MetricSnapshot
{
    public required string ModelKey { get; init; }
    public double? Threshold { get; init; }
    public int K { get; init; }
    public Dictionary<string, MetricValue> Values { get; init; } = [];
    public int EvaluatedItems { get; init; }
    public int RetainedResults { get; init; }
    public int EmptyQueries { get; init; }
    public int FailedItems { get; init; }

    public double? this[string metricId] => Values.TryGetValue(metricId, out var v) ? v.Value : null;
}

public sealed class ThresholdCurve
{
    public required string ModelKey { get; init; }
    public required double[] Thresholds { get; init; }
    public Dictionary<string, double?[]> Series { get; init; } = [];
}

public sealed class ThresholdEngine(MetricRegistry registry)
{
    public MetricRegistry Registry { get; } = registry;

    /// <summary>Recomputes metrics from cached scores. Never triggers inference.</summary>
    public MetricSnapshot Evaluate(EvalDataset dataset, ModelRunResult result, double? threshold, int k, IEnumerable<string> metricIds)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), "K must be positive.");
        if (threshold is { } limit && !double.IsFinite(limit)) throw new ArgumentException("Threshold must be finite.", nameof(threshold));
        var ctx = new MetricContext { Dataset = dataset, Result = result, Threshold = threshold, K = k };
        var values = new Dictionary<string, MetricValue>();
        foreach (var id in metricIds.Distinct())
        {
            var m = Registry.Find(id);
            if (m is null) { values[id] = MetricValue.NotAvailable(id, "Unknown metric"); continue; }
            if (!m.Definition.Tasks.Contains(dataset.Task)) { values[id] = MetricValue.NotAvailable(id, $"Not valid for {dataset.Task}"); continue; }
            try { values[id] = m.Compute(ctx); }
            catch (Exception ex) { values[id] = MetricValue.NotAvailable(id, "Metric failed: " + ex.Message); }
        }
        bool retrieval = dataset.Queries.Count > 0 && dataset.Task is EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking;
        return new MetricSnapshot
        {
            ModelKey = result.Model.Key,
            Threshold = threshold,
            K = k,
            Values = values,
            EvaluatedItems = retrieval ? ctx.EvaluatedQueries.Count() : result.Examples.Count(e => e.Error is null),
            RetainedResults = retrieval ? ctx.EvaluatedQueries.Sum(q => q.Retained.Count) : 0,
            EmptyQueries = retrieval ? ctx.EvaluatedQueries.Count(q => q.Retained.Count == 0) : 0,
            FailedItems = retrieval ? ctx.RetainedQueries.Count(q => q.Failed) : dataset.Examples.Count - result.Examples.Count(e => e.Error is null)
        };
    }

    /// <summary>Score range across the given results (used for slider bounds and shared curve axes).</summary>
    public static (double Min, double Max) ScoreRange(IEnumerable<ModelRunResult> results)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var r in results)
            foreach (var s in r.AllScores()) { if (s < min) min = s; if (s > max) max = s; }
        if (double.IsInfinity(min)) return (0, 1);
        if (min == max) { min -= 0.5; max += 0.5; }
        return (min, max);
    }

    /// <summary>Evenly spaced candidate thresholds over [min, max] (inclusive).</summary>
    public static double[] CandidateThresholds(double min, double max, int steps = 60)
    {
        steps = Math.Clamp(steps, 2, 1000);
        return [.. Enumerable.Range(0, steps).Select(i => min + (max - min) * i / (steps - 1))];
    }

    public ThresholdCurve Sweep(EvalDataset dataset, ModelRunResult result, double[] thresholds, int k, IReadOnlyList<string> metricIds, CancellationToken ct = default)
    {
        var series = metricIds.ToDictionary(id => id, _ => new double?[thresholds.Length]);
        for (int i = 0; i < thresholds.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var snap = Evaluate(dataset, result, thresholds[i], k, metricIds);
            foreach (var id in metricIds) series[id][i] = snap[id];
        }
        return new ThresholdCurve { ModelKey = result.Model.Key, Thresholds = thresholds, Series = series };
    }
}

public sealed record HistogramBin(double Start, double End, int Count);

public static class ScoreHistogram
{
    public static IReadOnlyList<HistogramBin> Build(IEnumerable<double> scores, double min, double max, int bins = 30)
    {
        var counts = new int[bins];
        double width = (max - min) / bins;
        if (width <= 0) width = 1;
        foreach (var s in scores)
        {
            int b = (int)Math.Floor((s - min) / width);
            counts[Math.Clamp(b, 0, bins - 1)]++;
        }
        return [.. Enumerable.Range(0, bins).Select(i => new HistogramBin(min + i * width, min + (i + 1) * width, counts[i]))];
    }
}

public static class ConfusionMatrix
{
    public sealed record Result(IReadOnlyList<string> Labels, int[,] Counts);

    public static Result? Compute(EvalDataset dataset, ModelRunResult run, double? threshold)
    {
        var ctx = new MetricContext { Dataset = dataset, Result = run, Threshold = threshold };
        var items = ctx.ClassifiedExamples().ToList();
        if (items.Count == 0) return null;
        var labels = items.Select(i => i.Example.Label!).Concat(items.Select(i => i.Predicted)).Distinct().OrderBy(l => l).ToList();
        var counts = new int[labels.Count, labels.Count];
        foreach (var (ex, _, pred) in items) counts[labels.IndexOf(ex.Label!), labels.IndexOf(pred)]++;
        return new(labels, counts);
    }
}
