using SemanticModelEvaluator.Core.Comparison;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;
using Xunit;

namespace SemanticModelEvaluator.Tests;

public class MetricTests
{
    private static readonly ThresholdEngine Engine = new(new MetricRegistry());
    private static readonly string[] RetrievalIds =
        ["precision_at_k", "recall_at_k", "mrr", "ndcg_at_k", "hit_rate_at_k", "empty_query_rate", "mean_retained", "judged_at_k", "harm_rate_at_k", "harmful_query_rate", "error_rate", "latency_mean_ms"];

    [Fact]
    public void Retrieval_metrics_match_hand_computed_values_without_threshold()
    {
        var s = Engine.Evaluate(Fixtures.Retrieval(), Fixtures.RetrievalRunA(), null, 3, RetrievalIds);
        Assert.Equal(1.0 / 6, s["precision_at_k"]!.Value, 6);   // q1 1/3, q2 0/1, q3 excluded (no relevant judgments)
        Assert.Equal(0.25, s["recall_at_k"]!.Value, 6);          // q1 1/2, q2 0
        Assert.Equal(0.25, s["mrr"]!.Value, 6);                  // q1 1/2, q2 0
        double ndcgQ1 = (3 / Math.Log2(3)) / (3 + 1 / Math.Log2(3));
        Assert.Equal(ndcgQ1 / 2, s["ndcg_at_k"]!.Value, 6);
        Assert.Equal(0.5, s["hit_rate_at_k"]!.Value, 6);
        Assert.Equal(0, s["empty_query_rate"]!.Value, 6);
        Assert.Equal(5.0 / 3, s["mean_retained"]!.Value, 6);
        Assert.Equal(2.0 / 5, s["judged_at_k"]!.Value, 6);       // q1-d2, q1-d1 judged of 5 retained
        Assert.Equal(0.5, s["harm_rate_at_k"]!.Value, 6);        // judged: d2 harmful, d1 safe
        Assert.Equal(3, s.Values["harm_rate_at_k"].MissingCount); // unjudged results are not counted as safe
        // q1 retains a harmful result; q2/q3 retain only unjudged results, so their safety is unknown and excluded (never assumed safe).
        Assert.Equal(1.0, s["harmful_query_rate"]!.Value, 6);
        Assert.Equal(2, s.Values["harmful_query_rate"].MissingCount);
        Assert.Equal(0, s["error_rate"]!.Value, 6);
        Assert.Equal(20, s["latency_mean_ms"]!.Value, 6);
        Assert.Contains("1 queries without relevant judgments excluded", s.Values["precision_at_k"].Note);
    }

    [Fact]
    public void Threshold_recalculates_from_cached_scores()
    {
        var ds = Fixtures.Retrieval();
        var run = Fixtures.RetrievalRunA();
        var s = Engine.Evaluate(ds, run, 0.6, 3, RetrievalIds);
        Assert.Equal(1.0 / 6, s["precision_at_k"]!.Value, 6);    // q1 1/3, q2 0/3
        Assert.Equal(0.25, s["recall_at_k"]!.Value, 6);
        Assert.Equal(1.0 / 3, s["empty_query_rate"]!.Value, 6);  // q2
        Assert.Equal(1.0, s["mean_retained"]!.Value, 6);         // (2 + 0 + 1) / 3
        Assert.Equal(1, s.EmptyQueries);
        Assert.Equal(3, s.RetainedResults);
        // Underlying run is untouched (cached scores, no inference).
        Assert.Equal(3, run.Queries[0].Results.Count);

        var high = Engine.Evaluate(ds, run, 0.95, 3, RetrievalIds);
        Assert.Equal(1.0, high["empty_query_rate"]!.Value, 6);
        Assert.Equal(0, high["precision_at_k"]);
    }

    [Fact]
    public void K_cutoff_limits_retained_results()
    {
        var s = Engine.Evaluate(Fixtures.Retrieval(), Fixtures.RetrievalRunA(), null, 1, RetrievalIds);
        Assert.Equal(0.0, s["precision_at_k"]!.Value, 6);        // q1 top1 = d2 (grade 0); q2 d5 unjudged
        Assert.Equal(1.0, s["mean_retained"]!.Value, 6);
    }

    [Fact]
    public void Missing_queries_are_failures_not_zeroes()
    {
        var run = Fixtures.RetrievalRunA();
        run.Queries.RemoveAt(1); // q2 missing
        var s = Engine.Evaluate(Fixtures.Retrieval(), run, null, 3, RetrievalIds);
        Assert.Equal(1.0 / 3, s["precision_at_k"]!.Value, 6);    // only q1 counts
        Assert.Equal(1, s.Values["precision_at_k"].MissingCount);
        Assert.Equal(1.0 / 3, s["error_rate"]!.Value, 6);
    }

    [Fact]
    public void Classification_metrics_match_hand_computed_values()
    {
        string[] ids = ["accuracy", "precision", "recall", "f1", "roc_auc", "pr_auc", "error_rate"];
        var s = Engine.Evaluate(Fixtures.Classification(), Fixtures.ClassificationRun(), 0.5, 10, ids);
        Assert.Equal(0.5, s["accuracy"]!.Value, 6);
        Assert.Equal(0.5, s["precision"]!.Value, 6);
        Assert.Equal(0.5, s["recall"]!.Value, 6);
        Assert.Equal(0.5, s["f1"]!.Value, 6);
        Assert.Equal(0.75, s["roc_auc"]!.Value, 6);
        Assert.Equal((1 + 2.0 / 3) / 2, s["pr_auc"]!.Value, 6);
        Assert.Equal(1, s.Values["accuracy"].MissingCount);      // e5 labelled but unpredicted; e6 unlabelled ignored
        Assert.Equal(1.0 / 6, s["error_rate"]!.Value, 6);

        var low = Engine.Evaluate(Fixtures.Classification(), Fixtures.ClassificationRun(), 0.3, 10, ids);
        Assert.Equal(2.0 / 3, low["precision"]!.Value, 6);
        Assert.Equal(1.0, low["recall"]!.Value, 6);
        Assert.Equal(0.75, low["roc_auc"]!.Value, 6);           // threshold-independent
    }

    [Fact]
    public void Roc_auc_handles_ties_as_half()
    {
        Assert.Equal(0.5, BuiltInMetrics.RocAuc([(true, 0.5), (false, 0.5)])!.Value, 6);
    }

    [Fact]
    public void Similarity_correlations_are_computed()
    {
        var ds = new Core.Datasets.EvalDataset
        {
            Id = "sts", Name = "sts", Task = EvalTask.SemanticSimilarity,
            Examples = [new("p1", "a", "b", GoldScore: 1), new("p2", "a", "b", GoldScore: 2), new("p3", "a", "b", GoldScore: 3), new("p4", "a", "b", GoldScore: 4)]
        };
        var run = new Core.Evaluation.ModelRunResult
        {
            Model = new("t", "m", "1"),
            Examples = [new() { ExampleId = "p1", Score = 0.1 }, new() { ExampleId = "p2", Score = 0.3 }, new() { ExampleId = "p3", Score = 0.2 }, new() { ExampleId = "p4", Score = 0.9 }]
        };
        var s = Engine.Evaluate(ds, run, null, 10, ["spearman", "pearson"]);
        Assert.Equal(0.8, s["spearman"]!.Value, 6);
        Assert.InRange(s["pearson"]!.Value, 0.5, 1);
    }

    [Fact]
    public void Registry_only_offers_metrics_supported_by_task_and_judgments()
    {
        var reg = new MetricRegistry();
        var compatible = reg.Compatible(EvalTask.Retrieval, Fixtures.Retrieval()).Select(m => m.Id).ToList();
        Assert.Contains("ndcg_at_k", compatible);
        Assert.Contains("harm_rate_at_k", compatible);
        Assert.DoesNotContain("accuracy", compatible);

        var noSafety = Fixtures.Retrieval();
        noSafety.Judgments.Clear();
        noSafety.Judgments.Add(new("q1", "d1", 1));
        var c2 = reg.Compatible(EvalTask.Retrieval, noSafety).Select(m => m.Id).ToList();
        Assert.DoesNotContain("harm_rate_at_k", c2);
        Assert.Contains(reg.Incompatible(EvalTask.Retrieval, noSafety), x => x.Metric.Id == "harm_rate_at_k");
    }

    [Fact]
    public void Every_metric_declares_required_metadata()
    {
        foreach (var m in new MetricRegistry().All.Select(m => m.Definition))
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Id));
            Assert.False(string.IsNullOrWhiteSpace(m.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(m.Aggregation));
            Assert.False(string.IsNullOrWhiteSpace(m.MissingDataPolicy));
            Assert.False(string.IsNullOrWhiteSpace(m.Version));
            Assert.NotEmpty(m.Tasks);
        }
    }

    [Fact]
    public void Sweep_produces_monotone_retained_counts()
    {
        var ds = Fixtures.Retrieval();
        var run = Fixtures.RetrievalRunA();
        var (min, max) = ThresholdEngine.ScoreRange([run]);
        Assert.Equal(0.3, min, 6);
        Assert.Equal(0.9, max, 6);
        var thresholds = ThresholdEngine.CandidateThresholds(min, max, 10);
        var curve = Engine.Sweep(ds, run, thresholds, 3, ["mean_retained", "precision_at_k"]);
        var retained = curve.Series["mean_retained"];
        for (int i = 1; i < retained.Length; i++) Assert.True(retained[i] <= retained[i - 1]);
    }

    [Fact]
    public void Confusion_matrix_counts_threshold_predictions()
    {
        var cm = ConfusionMatrix.Compute(Fixtures.Classification(), Fixtures.ClassificationRun(), 0.5)!;
        int total = 0;
        foreach (var c in cm.Counts) total += c;
        Assert.Equal(4, total);
    }
}

public class ComparisonTests
{
    private static readonly MetricRegistry Registry = new();
    private static readonly ThresholdEngine Engine = new(Registry);
    private static readonly string[] Ids = ["precision_at_k", "ndcg_at_k", "empty_query_rate", "latency_mean_ms", "mean_retained"];

    private static List<ScenarioEvaluation> Evaluations()
    {
        var ds = Fixtures.Retrieval();
        var a = Fixtures.RetrievalRunA();
        var b = Fixtures.RetrievalRunB();
        var sa = new Scenario { Id = "A", Name = "A", ModelKey = a.Model.Key, K = 3 };
        var sb = new Scenario { Id = "B", Name = "B", ModelKey = b.Model.Key, K = 3 };
        return [new(sa, a, Engine.Evaluate(ds, a, null, 3, Ids)), new(sb, b, Engine.Evaluate(ds, b, null, 3, Ids))];
    }

    [Fact]
    public void Deltas_are_relative_to_reference_and_respect_direction()
    {
        var evals = Evaluations();
        var defs = Ids.Select(i => Registry.Find(i)!.Definition).ToList();
        var table = ComparisonEngine.Compare(evals, "A", defs);
        var p = table.Rows.Single(r => r.Metric.Id == "precision_at_k");
        Assert.Equal(DeltaAssessment.Reference, p.Cells[0].Assessment);
        Assert.Equal(0.5 - 1.0 / 6, p.Cells[1].Delta!.Value, 6);
        Assert.Equal(DeltaAssessment.Better, p.Cells[1].Assessment);
        var lat = table.Rows.Single(r => r.Metric.Id == "latency_mean_ms");
        Assert.Equal(DeltaAssessment.Worse, lat.Cells[1].Assessment); // lower is better, B is slower
        var neutral = table.Rows.Single(r => r.Metric.Id == "mean_retained");
        Assert.Equal(DeltaAssessment.NoDirection, neutral.Cells[1].Assessment);
    }

    [Fact]
    public void Switching_reference_negates_deltas()
    {
        var evals = Evaluations();
        var defs = Ids.Select(i => Registry.Find(i)!.Definition).ToList();
        var byA = ComparisonEngine.Compare(evals, "A", defs);
        var byB = ComparisonEngine.Compare(evals, "B", defs);
        foreach (var id in new[] { "precision_at_k", "ndcg_at_k" })
        {
            var dA = byA.Rows.Single(r => r.Metric.Id == id).Cells.Single(c => c.ScenarioId == "B").Delta!.Value;
            var dB = byB.Rows.Single(r => r.Metric.Id == id).Cells.Single(c => c.ScenarioId == "A").Delta!.Value;
            Assert.Equal(dA, -dB, 9);
        }
        Assert.Equal("B", byB.ReferenceScenarioId);
        Assert.Equal(DeltaAssessment.Worse, byB.Rows.Single(r => r.Metric.Id == "precision_at_k").Cells.Single(c => c.ScenarioId == "A").Assessment);
    }

    [Fact]
    public void Radar_uses_only_bounded_directional_metrics_and_explains_transform()
    {
        var evals = Evaluations();
        var defs = Ids.Select(i => Registry.Find(i)!.Definition).ToList();
        var radar = RadarProjector.Project(evals, defs);
        Assert.Contains(radar.Axes, a => a.MetricId == "precision_at_k");
        Assert.Contains(radar.Axes, a => a.MetricId == "empty_query_rate" && a.Transformation.Contains("1 −"));
        Assert.DoesNotContain(radar.Axes, a => a.MetricId == "latency_mean_ms");
        Assert.DoesNotContain(radar.Axes, a => a.MetricId == "mean_retained");
        Assert.NotEmpty(radar.Excluded);
    }

    [Fact]
    public void Ranking_excludes_scenarios_with_missing_metrics_and_shows_formula()
    {
        var evals = Evaluations();
        var policy = new RankingPolicy { Name = "test", Weights = { ["precision_at_k"] = 1, ["ndcg_at_k"] = 1 } };
        var result = RankingEngine.Rank(evals, policy, Registry);
        Assert.Equal("B", result.Rows.Single(r => r.Rank == 1).Scenario.Id);
        Assert.False(string.IsNullOrWhiteSpace(result.Formula));
        Assert.Equal(RankingEngine.MissingPolicyText, result.MissingDataPolicy);

        // Remove a metric from A → A must be excluded, never imputed.
        evals[0].Snapshot.Values.Remove("ndcg_at_k");
        var r2 = RankingEngine.Rank(evals, policy, Registry);
        var a = r2.Rows.Single(r => r.Scenario.Id == "A");
        Assert.Null(a.Rank);
        Assert.NotNull(a.ExclusionReason);
    }

    [Fact]
    public void Ranking_constraints_exclude_violators()
    {
        var evals = Evaluations();
        var policy = new RankingPolicy { Name = "c", Weights = { ["precision_at_k"] = 1 }, Constraints = [new("latency_mean_ms", 30, true)] };
        var r = RankingEngine.Rank(evals, policy, Registry);
        Assert.Null(r.Rows.Single(x => x.Scenario.Id == "B").Rank);
        Assert.Equal(1, r.Rows.Single(x => x.Scenario.Id == "A").Rank);
    }

    [Fact]
    public void Error_analysis_separates_shared_and_model_specific_failures()
    {
        var evals = Evaluations();
        var ea = ErrorAnalysis.Analyze(Fixtures.Retrieval(), evals);
        Assert.Contains(ea.All, f => f.ItemId == "q2" && f.ScenarioIds.SequenceEqual(["A"]));
        Assert.True(ea.ModelSpecific.ContainsKey("A"));
    }
}
