using System.Text.Json.Serialization;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Metrics;

[JsonConverter(typeof(JsonStringEnumConverter<Direction>))]
public enum Direction { HigherIsBetter, LowerIsBetter, Neutral }

[JsonConverter(typeof(JsonStringEnumConverter<MetricCategory>))]
public enum MetricCategory { Quality, Harm, Coverage, Performance, Distribution }

/// <summary>Inputs a metric needs. Metrics are only offered when the dataset/run provides all of them.</summary>
[Flags]
public enum MetricInput
{
    None = 0,
    RankedResults = 1,
    RelevanceJudgments = 2,
    SafetyJudgments = 4,
    Labels = 8,
    Scores = 16,
    GoldScores = 32,
    MeasuredPerformance = 64
}

public sealed record MetricDefinition(
    string Id,
    string Name,
    string Version,
    EvalTask[] Tasks,
    MetricInput RequiredInputs,
    double? Min,
    double? Max,
    Direction Direction,
    MetricCategory Category,
    string Aggregation,
    string MissingDataPolicy,
    string Explanation,
    bool ThresholdDependent,
    string Format = "0.000")
{
    [JsonIgnore] public bool IsBounded01 => Min == 0 && Max == 1;
    public string DirectionText => Direction switch
    {
        Direction.HigherIsBetter => "Higher is better",
        Direction.LowerIsBetter => "Lower is better",
        _ => "No preferred direction"
    };
}

public sealed record MetricValue(string MetricId, double? Value, int SampleCount, int JudgedCount = 0, int MissingCount = 0, string? Note = null)
{
    public static MetricValue NotAvailable(string id, string why) => new(id, null, 0, Note: why);
}

public interface IMetric
{
    MetricDefinition Definition { get; }
    MetricValue Compute(MetricContext context);
}

/// <summary>Per-query results after applying the threshold and the K cutoff.</summary>
public sealed class RetainedQuery
{
    public required string QueryId { get; init; }
    public required IReadOnlyList<ScoredDocument> Retained { get; init; }
    public int FilteredByThreshold { get; init; }
    public bool Failed { get; init; }
}

public sealed class MetricContext
{
    public required EvalDataset Dataset { get; init; }
    public required ModelRunResult Result { get; init; }
    /// <summary>Minimum score retained. Null = no threshold.</summary>
    public double? Threshold { get; init; }
    public int K { get; init; } = 10;

    private IReadOnlyList<RetainedQuery>? retained;

    /// <summary>All dataset queries; queries missing from the run are marked Failed and excluded from quality metrics.</summary>
    public IReadOnlyList<RetainedQuery> RetainedQueries => retained ??= BuildRetained();

    private IReadOnlyList<RetainedQuery> BuildRetained()
    {
        var map = Result.Queries.ToDictionary(q => q.QueryId, StringComparer.Ordinal);
        var list = new List<RetainedQuery>(Dataset.Queries.Count);
        foreach (var q in Dataset.Queries)
        {
            if (!map.TryGetValue(q.Id, out var r) || r.Error is not null)
            {
                list.Add(new RetainedQuery { QueryId = q.Id, Retained = [], Failed = true });
                continue;
            }
            var kept = new List<ScoredDocument>(Math.Min(K, r.Results.Count));
            int filtered = 0;
            foreach (var d in r.Results)
            {
                if (Threshold is { } t && d.Score < t) { filtered++; continue; }
                if (kept.Count < K) kept.Add(d);
            }
            list.Add(new RetainedQuery { QueryId = q.Id, Retained = kept, FilteredByThreshold = filtered });
        }
        return list;
    }

    public IEnumerable<RetainedQuery> EvaluatedQueries => RetainedQueries.Where(q => !q.Failed);

    /// <summary>Labelled examples that have a model prediction, with the binary prediction at the current threshold.</summary>
    public IEnumerable<(LabeledExample Example, ExampleResult Result, string Predicted)> ClassifiedExamples()
    {
        var map = Result.Examples.Where(e => e.Error is null).ToDictionary(e => e.ExampleId, StringComparer.Ordinal);
        string? positive = Dataset.PositiveLabel;
        string negative = Dataset.Examples.Select(e => e.Label).FirstOrDefault(l => l is not null && l != positive) ?? "negative";
        foreach (var ex in Dataset.Examples)
        {
            if (ex.Label is null || !map.TryGetValue(ex.ExampleId(), out var r)) continue;
            string? pred = r.Score.HasValue && positive is not null
                ? (r.Score.Value >= (Threshold ?? 0.5) ? positive : negative)
                : r.PredictedLabel;
            if (pred is null) continue;
            yield return (ex, r, pred);
        }
    }
}

internal static class ExampleExtensions
{
    public static string ExampleId(this LabeledExample e) => e.Id;
}
