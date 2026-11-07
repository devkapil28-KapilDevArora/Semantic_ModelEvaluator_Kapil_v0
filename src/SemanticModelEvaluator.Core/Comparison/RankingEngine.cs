using System.Globalization;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Comparison;

public sealed record RankingConstraint(string MetricId, double Limit, bool IsMaximum);

public sealed class RankingPolicy
{
    public string Name { get; set; } = "Custom";
    public Dictionary<string, double> Weights { get; set; } = [];
    public List<RankingConstraint> Constraints { get; set; } = [];

    public static IReadOnlyList<RankingPolicy> Presets(EvalTask task)
    {
        bool retrieval = task is EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking;
        bool classification = task is EvalTask.Classification or EvalTask.Safety;
        Dictionary<string, double> quality = retrieval ? new() { ["ndcg_at_k"] = 0.5, ["recall_at_k"] = 0.3, ["mrr"] = 0.2 }
            : classification ? new() { ["f1"] = 0.5, ["roc_auc"] = 0.3, ["accuracy"] = 0.2 }
            : new() { ["spearman"] = 1.0 };
        var balanced = quality.ToDictionary(k => k.Key, k => k.Value * 0.6);
        balanced["latency_mean_ms"] = 0.4;
        var safety = new RankingPolicy { Name = "Quality with safety constraint", Weights = new(quality) };
        if (retrieval) safety.Constraints.Add(new("harm_rate_at_k", 0.05, IsMaximum: true));
        return
        [
            new() { Name = "Quality first", Weights = quality },
            new() { Name = "Lowest latency", Weights = new() { ["latency_mean_ms"] = 1.0 } },
            safety,
            new() { Name = "Balanced quality and performance", Weights = balanced },
        ];
    }
}

public sealed record RankingContribution(string MetricId, double RawValue, double Utility, double Weight);

public sealed record RankingRow(Scenario Scenario, int? Rank, double? Score, IReadOnlyList<RankingContribution> Contributions, string? ExclusionReason);

public sealed class RankingResult
{
    public required RankingPolicy Policy { get; init; }
    public required IReadOnlyList<RankingRow> Rows { get; init; }
    public required string Formula { get; init; }
    public required string MissingDataPolicy { get; init; }
    public List<string> Notes { get; } = [];
}

public static class RankingEngine
{
    public const string MissingPolicyText = "A scenario missing any weighted metric, or violating a constraint, is excluded from ranking. Missing values are never imputed.";

    /// <summary>
    /// Score = Σ wᵢ·uᵢ / Σ wᵢ. For 0–1 metrics uᵢ = value (higher-better) or 1 − value (lower-better).
    /// For unbounded lower-is-better metrics (latency, memory) uᵢ = best / value across the compared scenarios;
    /// for unbounded higher-is-better metrics uᵢ = value / best. Neutral metrics cannot be weighted.
    /// </summary>
    public static RankingResult Rank(IReadOnlyList<ScenarioEvaluation> evaluations, RankingPolicy policy, MetricRegistry registry)
    {
        var notes = new List<string>();
        var weights = new Dictionary<string, (MetricDefinition Def, double W)>();
        foreach (var (id, w) in policy.Weights)
        {
            if (w <= 0) continue;
            var def = registry.Find(id)?.Definition;
            if (def is null) { notes.Add($"Unknown metric '{id}' ignored."); continue; }
            if (def.Direction == Direction.Neutral) { notes.Add($"{def.Name} has no preferred direction and cannot be weighted."); continue; }
            weights[id] = (def, w);
        }
        if (weights.Count == 0) notes.Add("No valid weighted metrics; nothing can be ranked.");

        var best = weights.Where(w => !w.Value.Def.IsBounded01).ToDictionary(w => w.Key, w =>
        {
            var vals = evaluations.Select(e => e.Snapshot[w.Key]).Where(v => v is > 0).Select(v => v!.Value).ToList();
            return vals.Count == 0 ? (double?)null : w.Value.Def.Direction == Direction.LowerIsBetter ? vals.Min() : vals.Max();
        });

        var rows = new List<RankingRow>();
        foreach (var e in evaluations)
        {
            string? excluded = null;
            foreach (var c in policy.Constraints)
            {
                var v = e.Snapshot[c.MetricId];
                if (v is null) { excluded = $"Constraint metric {c.MetricId} unavailable"; break; }
                if (c.IsMaximum ? v > c.Limit : v < c.Limit) { excluded = $"Violates {c.MetricId} {(c.IsMaximum ? "≤" : "≥")} {c.Limit.ToString(CultureInfo.InvariantCulture)} (actual {v:0.###})"; break; }
            }
            var contributions = new List<RankingContribution>();
            if (excluded is null)
                foreach (var (id, (def, w)) in weights)
                {
                    var v = e.Snapshot[id];
                    if (v is null) { excluded = $"Missing {def.Name}"; break; }
                    double u;
                    if (def.IsBounded01) u = def.Direction == Direction.LowerIsBetter ? 1 - v.Value : v.Value;
                    else if (best[id] is not { } b || v <= 0) { excluded = $"{def.Name} not comparable"; break; }
                    else u = def.Direction == Direction.LowerIsBetter ? b / v.Value : v.Value / b;
                    contributions.Add(new(id, v.Value, u, w));
                }
            double? score = excluded is null && contributions.Count > 0 ? contributions.Sum(c => c.Utility * c.Weight) / contributions.Sum(c => c.Weight) : null;
            if (excluded is null && contributions.Count == 0) excluded = "No weighted metrics";
            rows.Add(new RankingRow(e.Scenario, null, score, contributions, excluded));
        }
        int rank = 0;
        var ranked = rows.Where(r => r.Score is not null).OrderByDescending(r => r.Score).Select(r => r with { Rank = ++rank })
            .Concat(rows.Where(r => r.Score is null)).ToList();
        if (weights.Values.Any(w => !w.Def.IsBounded01))
            notes.Add("Unbounded metrics are scaled relative to the best compared scenario, so scores change when the scenario set changes.");
        if (evaluations.Select(e => e.Result.Performance.Device + e.Result.Performance.Runtime).Distinct().Count() > 1 && weights.Values.Any(w => w.Def.Category == MetricCategory.Performance))
            notes.Add("Performance measurements come from different devices/runtimes and may not be comparable.");
        if (evaluations.Any(e => e.Result.IsSynthetic)) notes.Add("Includes synthetic/deterministic baselines; their scores are not real model performance.");

        string formula = "score = (" + string.Join(" + ", weights.Select(w => $"{w.Value.W:0.##}·u({w.Value.Def.Name})")) + ") / " +
                         weights.Values.Sum(w => w.W).ToString("0.##", CultureInfo.InvariantCulture);
        var result = new RankingResult { Policy = policy, Rows = ranked, Formula = formula, MissingDataPolicy = MissingPolicyText };
        result.Notes.AddRange(notes);
        return result;
    }
}
