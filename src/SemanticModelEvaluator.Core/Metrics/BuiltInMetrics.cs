using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Metrics;

/// <summary>Metric implemented by a delegate; keeps definitions and formulas side by side.</summary>
public sealed class DelegateMetric(MetricDefinition definition, Func<MetricContext, MetricValue> compute) : IMetric
{
    public MetricDefinition Definition { get; } = definition;
    public MetricValue Compute(MetricContext context) => compute(context);
}

public static class BuiltInMetrics
{
    private static readonly EvalTask[] RetrievalTasks = [EvalTask.Retrieval, EvalTask.ImageTextRetrieval, EvalTask.Reranking];
    private static readonly EvalTask[] ClassTasks = [EvalTask.Classification, EvalTask.Safety];
    private static readonly EvalTask[] AllTasks = Enum.GetValues<EvalTask>();

    private const string RelevantOnly = "Queries without any relevant judgment are excluded; failed queries are excluded and counted as missing; unjudged retrieved documents count as non-relevant (see Judged@K).";

    public static IReadOnlyList<IMetric> Create() =>
    [
        Retrieval("precision_at_k", "Precision@K", "Relevant retained results divided by K. Empty result sets contribute zero; missing ranks are non-relevant.",
            (ctx, q, rel) => (double)q.Retained.Count(d => IsRelevant(ctx, q.QueryId, d.DocumentId)) / ctx.K,
            RelevantOnly),
        Retrieval("recall_at_k", "Recall@K", "Relevant documents retained in the top K divided by all relevant judged documents for the query.",
            (ctx, q, rel) => (double)q.Retained.Count(d => IsRelevant(ctx, q.QueryId, d.DocumentId)) / rel, RelevantOnly),
        Retrieval("mrr", "MRR@K", "Mean reciprocal rank of the first relevant retained result (0 when none is retained).",
            (ctx, q, rel) =>
            {
                for (int i = 0; i < q.Retained.Count; i++) if (IsRelevant(ctx, q.QueryId, q.Retained[i].DocumentId)) return 1.0 / (i + 1);
                return 0;
            }, RelevantOnly),
        Retrieval("ndcg_at_k", "NDCG@K", "Normalised discounted cumulative gain with gain 2^grade − 1 and discount log2(rank + 1); ideal ranking built from all judgments for the query.",
            (ctx, q, rel) => Ndcg(ctx, q), RelevantOnly),
        Retrieval("hit_rate_at_k", "Hit rate@K", "Share of queries with at least one relevant retained result in the top K.",
            (ctx, q, rel) => q.Retained.Any(d => IsRelevant(ctx, q.QueryId, d.DocumentId)) ? 1 : 0, RelevantOnly),
        Retrieval("map_at_k", "MAP@K", "Mean average precision: for each query, the sum of precision at every rank (≤ K) holding a relevant retained result, divided by min(relevant judged documents, K); averaged over queries. Binary relevance (grade > 0).",
            (ctx, q, rel) =>
            {
                double hits = 0, sum = 0;
                for (int i = 0; i < Math.Min(q.Retained.Count, ctx.K); i++)
                    if (IsRelevant(ctx, q.QueryId, q.Retained[i].DocumentId)) { hits++; sum += hits / (i + 1); }
                return sum / Math.Min(rel, ctx.K);
            }, RelevantOnly),

        new DelegateMetric(new("empty_query_rate", "Empty-query rate", "1", RetrievalTasks, MetricInput.RankedResults, 0, 1, Direction.LowerIsBetter,
            MetricCategory.Coverage, "Fraction of evaluated queries", "Failed queries are excluded and counted as missing.",
            "Share of queries for which no result survives the threshold.", true),
            ctx =>
            {
                var qs = ctx.EvaluatedQueries.ToList();
                int missing = ctx.RetainedQueries.Count - qs.Count;
                return qs.Count == 0 ? MetricValue.NotAvailable("empty_query_rate", "No evaluated queries")
                    : new("empty_query_rate", (double)qs.Count(q => q.Retained.Count == 0) / qs.Count, qs.Count, MissingCount: missing);
            }),
        new DelegateMetric(new("mean_retained", "Mean retained results", "1", RetrievalTasks, MetricInput.RankedResults, 0, null, Direction.Neutral,
            MetricCategory.Coverage, "Mean over evaluated queries", "Failed queries are excluded.",
            "Average number of results per query surviving the threshold (capped at K).", true, "0.00"),
            ctx =>
            {
                var qs = ctx.EvaluatedQueries.ToList();
                return qs.Count == 0 ? MetricValue.NotAvailable("mean_retained", "No evaluated queries")
                    : new("mean_retained", qs.Average(q => q.Retained.Count), qs.Count);
            }),
        new DelegateMetric(new("judged_at_k", "Judged@K", "1", RetrievalTasks, MetricInput.RankedResults | MetricInput.RelevanceJudgments, 0, 1, Direction.Neutral,
            MetricCategory.Coverage, "Pooled over all retained results", "Not available when nothing is retained.",
            "Share of retained results that carry a relevance judgment. Low values mean quality metrics are uncertain, not that the model is worse.", true),
            ctx =>
            {
                var all = ctx.EvaluatedQueries.SelectMany(q => q.Retained.Select(d => (q.QueryId, d.DocumentId))).ToList();
                if (all.Count == 0) return MetricValue.NotAvailable("judged_at_k", "No retained results");
                int judged = all.Count(x => ctx.Dataset.JudgmentIndex.Get(x.QueryId, x.DocumentId)?.Grade is not null);
                return new("judged_at_k", (double)judged / all.Count, all.Count, judged);
            }),
        new DelegateMetric(new("harm_rate_at_k", "Harmful result rate@K", "1", RetrievalTasks, MetricInput.RankedResults | MetricInput.SafetyJudgments, 0, 1, Direction.LowerIsBetter,
            MetricCategory.Harm, "Pooled over retained results with a safety judgment",
            "Results without a safety judgment are excluded (never assumed safe); their count is reported.",
            "Share of safety-judged retained results that are judged harmful.", true),
            ctx =>
            {
                var all = ctx.EvaluatedQueries.SelectMany(q => q.Retained.Select(d => ctx.Dataset.JudgmentIndex.Get(q.QueryId, d.DocumentId))).ToList();
                var judged = all.Where(j => j?.Harmful is not null).ToList();
                int unjudged = all.Count - judged.Count;
                if (judged.Count == 0) return MetricValue.NotAvailable("harm_rate_at_k", all.Count == 0 ? "No retained results" : $"None of {all.Count} retained results has a safety judgment");
                return new("harm_rate_at_k", (double)judged.Count(j => j!.Harmful == true) / judged.Count, all.Count, judged.Count, unjudged,
                    unjudged > 0 ? $"{unjudged} retained results lack a safety judgment and are excluded." : null);
            }),
        new DelegateMetric(new("harmful_query_rate", "Queries with harmful result", "1", RetrievalTasks, MetricInput.RankedResults | MetricInput.SafetyJudgments, 0, 1, Direction.LowerIsBetter,
            MetricCategory.Harm, "Fraction of queries whose safety is known",
            "A query counts as harmful if any retained result is judged harmful, and as safe only if every retained result has a safety judgment (or nothing is retained). Queries with unjudged results and no harmful result are unknown and excluded (never assumed safe).",
            "Share of safety-known queries with at least one retained result judged harmful.", true),
            ctx =>
            {
                var qs = ctx.EvaluatedQueries.ToList();
                if (qs.Count == 0) return MetricValue.NotAvailable("harmful_query_rate", "No evaluated queries");
                int harmful = 0, safe = 0, unknown = 0;
                foreach (var q in qs)
                {
                    var js = q.Retained.Select(d => ctx.Dataset.JudgmentIndex.Get(q.QueryId, d.DocumentId)?.Harmful).ToList();
                    if (js.Any(h => h == true)) harmful++;
                    else if (js.All(h => h is not null)) safe++;
                    else unknown++;
                }
                if (harmful + safe == 0) return MetricValue.NotAvailable("harmful_query_rate", $"Safety is unknown for all {qs.Count} queries (retained results lack safety judgments)");
                return new("harmful_query_rate", (double)harmful / (harmful + safe), qs.Count, harmful + safe, unknown,
                    unknown > 0 ? $"{unknown} queries have unjudged results and no harmful result; their safety is unknown and they are excluded." : null);
            }),

        Classification("accuracy", "Accuracy", "Share of labelled examples whose prediction equals the label.",
            items => (double)items.Count(i => i.Predicted == i.Label) / items.Count),
        Classification("precision", "Precision", "TP / (TP + FP) for the positive class.",
            items => Ratio(items.Count(i => i.Predicted == i.Positive && i.Label == i.Positive), items.Count(i => i.Predicted == i.Positive)), binary: true),
        Classification("recall", "Recall", "TP / (TP + FN) for the positive class.",
            items => Ratio(items.Count(i => i.Predicted == i.Positive && i.Label == i.Positive), items.Count(i => i.Label == i.Positive)), binary: true),
        Classification("f1", "F1", "Harmonic mean of precision and recall for the positive class.",
            items =>
            {
                double tp = items.Count(i => i.Predicted == i.Positive && i.Label == i.Positive);
                double fp = items.Count(i => i.Predicted == i.Positive && i.Label != i.Positive);
                double fn = items.Count(i => i.Predicted != i.Positive && i.Label == i.Positive);
                return tp + fp + fn == 0 ? null : 2 * tp / (2 * tp + fp + fn);
            }, binary: true),
        Classification("macro_f1", "Macro F1", "Unweighted mean of per-class F1 over all labels present.",
            items =>
            {
                var labels = items.Select(i => i.Label).Distinct().ToList();
                return labels.Average(l =>
                {
                    double tp = items.Count(i => i.Predicted == l && i.Label == l);
                    double fp = items.Count(i => i.Predicted == l && i.Label != l);
                    double fn = items.Count(i => i.Predicted != l && i.Label == l);
                    return tp + fp + fn == 0 ? 0 : 2 * tp / (2 * tp + fp + fn);
                });
            }),
        ScoreRanking("roc_auc", "ROC-AUC", "Probability that a random positive example scores above a random negative (ties count ½). Threshold-independent.", RocAuc),
        ScoreRanking("pr_auc", "PR-AUC (average precision)", "Average precision of the score ranking for the positive class. Threshold-independent.", AveragePrecision),

        new DelegateMetric(new("spearman", "Spearman ρ", "1", [EvalTask.SemanticSimilarity], MetricInput.Scores | MetricInput.GoldScores, -1, 1, Direction.HigherIsBetter,
            MetricCategory.Quality, "Rank correlation over pairs", "Pairs without a gold score or model score are excluded.",
            "Spearman rank correlation between model similarity and gold similarity.", false),
            ctx => Correlation(ctx, "spearman", rank: true)),
        new DelegateMetric(new("pearson", "Pearson r", "1", [EvalTask.SemanticSimilarity], MetricInput.Scores | MetricInput.GoldScores, -1, 1, Direction.HigherIsBetter,
            MetricCategory.Quality, "Linear correlation over pairs", "Pairs without a gold score or model score are excluded.",
            "Pearson correlation between model similarity and gold similarity.", false),
            ctx => Correlation(ctx, "pearson", rank: false)),

        new DelegateMetric(new("error_rate", "Failed items", "1", AllTasks, MetricInput.None, 0, 1, Direction.LowerIsBetter,
            MetricCategory.Coverage, "Fraction of dataset items", "None.", "Share of dataset items for which the model produced no output (error or missing prediction).", false),
            ctx =>
            {
                if (ctx.Dataset.Queries.Count > 0 && ctx.Dataset.Task is EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking)
                    return new("error_rate", (double)ctx.RetainedQueries.Count(q => q.Failed) / ctx.RetainedQueries.Count, ctx.RetainedQueries.Count);
                if (ctx.Dataset.Examples.Count == 0) return MetricValue.NotAvailable("error_rate", "Empty dataset");
                var ok = ctx.Result.Examples.Where(e => e.Error is null).Select(e => e.ExampleId).ToHashSet();
                return new("error_rate", (double)ctx.Dataset.Examples.Count(e => !ok.Contains(e.Id)) / ctx.Dataset.Examples.Count, ctx.Dataset.Examples.Count);
            }),

        Perf("latency_mean_ms", "Mean query latency (ms)", Direction.LowerIsBetter, "Mean wall-clock time to encode and score one query (documents pre-encoded).", p => p.MeanLatencyMs, "0.0"),
        Perf("latency_p95_ms", "P95 query latency (ms)", Direction.LowerIsBetter, "95th percentile per-query latency.", p => p.P95LatencyMs, "0.0"),
        Perf("throughput_per_s", "Document throughput (/s)", Direction.HigherIsBetter, "Documents encoded per second during corpus encoding.", p => p.ThroughputPerSecond, "0.0"),
        Perf("load_time_ms", "Model load time (ms)", Direction.LowerIsBetter, "Time to load the model and create the inference session.", p => p.LoadMs, "0"),
        Perf("peak_memory_mb", "Peak process memory (MB)", Direction.LowerIsBetter, "Peak working set of the evaluation process observed after this model ran (includes the application itself).", p => p.PeakWorkingSetBytes / 1048576.0, "0"),
    ];

    private static bool IsRelevant(MetricContext ctx, string q, string d) => ctx.Dataset.JudgmentIndex.Get(q, d)?.Grade > 0;

    internal static double Ndcg(MetricContext ctx, RetainedQuery q)
    {
        double dcg = 0;
        for (int i = 0; i < q.Retained.Count; i++)
        {
            double g = ctx.Dataset.JudgmentIndex.Get(q.QueryId, q.Retained[i].DocumentId)?.Grade ?? 0;
            if (g > 0) dcg += (Math.Pow(2, g) - 1) / Math.Log2(i + 2);
        }
        var ideal = ctx.Dataset.JudgmentIndex.IdealGrades(q.QueryId);
        double idcg = 0;
        for (int i = 0; i < Math.Min(ctx.K, ideal.Length); i++) idcg += (Math.Pow(2, ideal[i]) - 1) / Math.Log2(i + 2);
        return idcg == 0 ? 0 : dcg / idcg;
    }

    private static IMetric Retrieval(string id, string name, string explanation, Func<MetricContext, RetainedQuery, int, double?> perQuery, string missingPolicy) =>
        new DelegateMetric(new(id, name, id == "precision_at_k" ? "2" : "1", RetrievalTasks, MetricInput.RankedResults | MetricInput.RelevanceJudgments, 0, 1, Direction.HigherIsBetter,
            MetricCategory.Quality, "Macro-average over queries", missingPolicy, explanation, true),
            ctx =>
            {
                var values = new List<double>();
                int missing = ctx.RetainedQueries.Count(q => q.Failed), unjudged = 0, excluded = 0;
                foreach (var q in ctx.EvaluatedQueries)
                {
                    int rel = ctx.Dataset.JudgmentIndex.RelevantCount(q.QueryId);
                    if (rel == 0) { unjudged++; continue; }
                    var v = perQuery(ctx, q, rel);
                    if (v is null) { excluded++; continue; }
                    values.Add(v.Value);
                }
                if (values.Count == 0) return MetricValue.NotAvailable(id, "No query with relevance judgments" + (excluded > 0 ? " and retained results" : ""));
                var notes = new List<string>();
                if (unjudged > 0) notes.Add($"{unjudged} queries without relevant judgments excluded");
                if (excluded > 0) notes.Add($"{excluded} queries with no retained results excluded");
                return new(id, values.Average(), values.Count, values.Count, missing, notes.Count > 0 ? string.Join("; ", notes) + "." : null);
            });

    private sealed record ClassItem(string Label, string Predicted, string? Positive, double? Score);

    private static IMetric Classification(string id, string name, string explanation, Func<List<ClassItem>, double?> f, bool binary = false) =>
        new DelegateMetric(new(id, name, "1", ClassTasks, MetricInput.Labels, 0, 1, Direction.HigherIsBetter, MetricCategory.Quality,
            "Pooled over labelled examples", "Unlabelled examples and examples without a prediction are excluded (never treated as negative/safe).",
            explanation + (binary ? " Binary predictions use score ≥ threshold when scores exist." : ""), true),
            ctx =>
            {
                if (binary && ctx.Dataset.PositiveLabel is null) return MetricValue.NotAvailable(id, "Dataset does not define a positive label.");
                var items = ctx.ClassifiedExamples().Select(x => new ClassItem(x.Example.Label!, x.Predicted, ctx.Dataset.PositiveLabel, x.Result.Score)).ToList();
                int labelled = ctx.Dataset.Examples.Count(e => e.Label is not null);
                if (items.Count == 0) return MetricValue.NotAvailable(id, "No labelled example has a prediction.");
                var v = f(items);
                return v is null ? MetricValue.NotAvailable(id, "Undefined (zero denominator).")
                    : new(id, v, items.Count, items.Count, labelled - items.Count);
            });

    private static IMetric ScoreRanking(string id, string name, string explanation, Func<List<(bool Positive, double Score)>, double?> f) =>
        new DelegateMetric(new(id, name, id == "pr_auc" ? "2" : "1", ClassTasks, MetricInput.Labels | MetricInput.Scores, 0, 1, Direction.HigherIsBetter, MetricCategory.Quality,
            "Pooled over labelled, scored examples", "Requires a positive label, scores, and both classes; otherwise not available.", explanation, false),
            ctx =>
            {
                var pos = ctx.Dataset.PositiveLabel;
                if (pos is null) return MetricValue.NotAvailable(id, "Dataset does not define a positive label.");
                var map = ctx.Result.Examples.Where(e => e.Error is null && e.Score.HasValue).ToDictionary(e => e.ExampleId);
                var items = ctx.Dataset.Examples.Where(e => e.Label is not null && map.ContainsKey(e.Id))
                    .Select(e => (e.Label == pos, map[e.Id].Score!.Value)).ToList();
                if (items.Count == 0 || items.All(i => i.Item1) || items.All(i => !i.Item1))
                    return MetricValue.NotAvailable(id, "Both classes with scores are required.");
                return new(id, f(items), items.Count, items.Count);
            });

    internal static double? RocAuc(List<(bool Positive, double Score)> items)
    {
        var sorted = items.OrderBy(i => i.Score).ToList();
        double rankSum = 0;
        int i = 0;
        while (i < sorted.Count)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1].Score == sorted[i].Score) j++;
            double avgRank = (i + j) / 2.0 + 1;
            for (int k = i; k <= j; k++) if (sorted[k].Positive) rankSum += avgRank;
            i = j + 1;
        }
        double nPos = items.Count(x => x.Positive), nNeg = items.Count - nPos;
        return (rankSum - nPos * (nPos + 1) / 2) / (nPos * nNeg);
    }

    internal static double? AveragePrecision(List<(bool Positive, double Score)> items)
    {
        var sorted = items.OrderByDescending(i => i.Score).ToList();
        double nPos = items.Count(x => x.Positive), tp = 0, sum = 0;
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            double positives = 0;
            while (j < sorted.Count && sorted[j].Score == sorted[i].Score)
            {
                if (sorted[j].Positive) positives++;
                j++;
            }
            tp += positives;
            sum += positives * tp / j;
            i = j;
        }
        return nPos == 0 ? null : sum / nPos;
    }

    private static MetricValue Correlation(MetricContext ctx, string id, bool rank)
    {
        var map = ctx.Result.Examples.Where(e => e.Error is null && e.Score.HasValue).ToDictionary(e => e.ExampleId);
        var pairs = ctx.Dataset.Examples.Where(e => e.GoldScore.HasValue && map.ContainsKey(e.Id))
            .Select(e => (Gold: e.GoldScore!.Value, Model: map[e.Id].Score!.Value)).ToList();
        if (pairs.Count < 3) return MetricValue.NotAvailable(id, "At least 3 scored pairs with gold scores are required.");
        var x = pairs.Select(p => p.Model).ToArray();
        var y = pairs.Select(p => p.Gold).ToArray();
        if (rank) { x = Ranks(x); y = Ranks(y); }
        return new(id, Pearson(x, y), pairs.Count, pairs.Count, ctx.Dataset.Examples.Count - pairs.Count);
    }

    internal static double[] Ranks(double[] v)
    {
        var idx = Enumerable.Range(0, v.Length).OrderBy(i => v[i]).ToArray();
        var r = new double[v.Length];
        int a = 0;
        while (a < idx.Length)
        {
            int b = a;
            while (b + 1 < idx.Length && v[idx[b + 1]] == v[idx[a]]) b++;
            for (int k = a; k <= b; k++) r[idx[k]] = (a + b) / 2.0 + 1;
            a = b + 1;
        }
        return r;
    }

    internal static double? Pearson(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
        return sxx == 0 || syy == 0 ? null : sxy / Math.Sqrt(sxx * syy);
    }

    private static double? Ratio(int a, int b) => b == 0 ? null : (double)a / b;

    private static IMetric Perf(string id, string name, Direction dir, string explanation, Func<Evaluation.PerformanceStats, double?> get, string fmt) =>
        new DelegateMetric(new(id, name, "1", AllTasks, MetricInput.MeasuredPerformance, 0, null, dir, MetricCategory.Performance,
            "Single measurement per run", "Not available for imported predictions or when not measured.",
            explanation + " Only comparable between runs on the same hardware and runtime configuration.", false, fmt),
            ctx => ctx.Result.Performance.Measured && get(ctx.Result.Performance) is { } v
                ? new(id, v, 1, Note: $"{ctx.Result.Performance.Runtime} on {ctx.Result.Performance.Device}")
                : MetricValue.NotAvailable(id, "Not measured for this model (e.g. imported predictions)."));
}
