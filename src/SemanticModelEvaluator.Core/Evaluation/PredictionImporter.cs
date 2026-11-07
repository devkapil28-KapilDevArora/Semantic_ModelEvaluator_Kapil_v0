using System.Globalization;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Evaluation;

/// <summary>
/// Imports predictions produced elsewhere. Required column names (case-insensitive):
/// retrieval: query_id, document_id (or doc_id), score [, rank, latency_ms];
/// examples:  example_id (or id), and score and/or predicted_label [, output, latency_ms].
/// </summary>
public static class PredictionImporter
{
    public static ModelRunResult Import(string path, ModelIdentity identity, string displayName, EvalDataset dataset, int retainDepth = 1000)
    {
        var table = TabularReader.Read(path);
        var result = new ModelRunResult
        {
            Model = identity,
            DisplayName = displayName,
            Execution = ExecutionKind.ImportedPredictions,
            Status = RunStatus.Completed,
            Performance = new PerformanceStats { Measured = false, Runtime = "Imported predictions", Device = "unknown" }
        };
        int Col(params string[] names) => names.Select(table.IndexOf).FirstOrDefault(i => i >= 0, -1);
        static double? Num(string?[] r, int i)
        {
            if (i < 0 || i >= r.Length || string.IsNullOrWhiteSpace(r[i])) return null;
            if (!double.TryParse(r[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
                throw new InvalidDataException("Prediction contains an invalid or non-finite numeric value.");
            return v;
        }
        int latency = Col("latency_ms", "latency");
        bool retrieval = dataset.Task is EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking;
        if (retrieval)
        {
            int q = Col("query_id", "qid", "queryid"), d = Col("document_id", "doc_id", "docid", "documentid"), s = Col("score"), rank = Col("rank");
            if (q < 0 || d < 0 || (s < 0 && rank < 0))
                throw new InvalidDataException("Retrieval predictions need columns query_id, document_id and score (or rank).");
            var known = dataset.Queries.Select(x => x.Id).ToHashSet();
            int unknown = 0, bad = 0;
            foreach (var g in table.Rows.GroupBy(r => r[q] ?? ""))
            {
                if (!known.Contains(g.Key)) { unknown++; result.Warnings.Add($"Unknown query '{g.Key}' ignored."); continue; }
                var docs = new List<(string Id, double Score, double Rank)>();
                foreach (var r in g)
                {
                    var score = Num(r, s);
                    var rk = Num(r, rank);
                    if (r[d] is not { Length: > 0 } id || (score is null && rk is null)) { bad++; continue; }
                    if (dataset.FindDocument(id) is null) { result.Warnings.Add($"Unknown document '{id}' ignored."); continue; }
                    if (docs.Any(x => x.Id == id)) throw new InvalidDataException($"Duplicate prediction for query '{g.Key}', document '{id}'.");
                    // When only ranks exist, a score of −rank preserves order; thresholds then operate on rank.
                    if (score is null) result.ScoresIncludeRanks = true;
                    docs.Add((id, score ?? -rk!.Value, rk ?? 0));
                }
                result.Queries.Add(new QueryResult
                {
                    QueryId = g.Key,
                    Results = [.. docs.OrderByDescending(x => x.Score).ThenBy(x => x.Rank).Take(retainDepth).Select(x => new ScoredDocument(x.Id, x.Score))],
                    LatencyMs = g.Select(r => Num(r, latency)).FirstOrDefault(x => x.HasValue)
                });
            }
            if (s < 0) result.Warnings.Add("Predictions contain ranks but no scores; thresholds are applied to −rank.");
            else if (result.ScoresIncludeRanks) result.Warnings.Add("Some prediction rows have a rank but no score; −rank is used for them, so a threshold mixes score and rank scales.");
            if (unknown > 0) result.Warnings.Add($"{unknown} predicted queries are not in the dataset and were ignored.");
            if (bad > 0) result.Warnings.Add($"{bad} prediction rows were incomplete and ignored.");
            int missing = dataset.Queries.Count - result.Queries.Count;
            if (missing > 0) { result.Warnings.Add($"{missing} dataset queries have no predictions (counted as failed)."); result.FailedItems = missing; }
        }
        else
        {
            int id = Col("example_id", "id"), s = Col("score", "probability", "similarity"), p = Col("predicted_label", "prediction", "predicted"), o = Col("output", "generated");
            if (id < 0 || (s < 0 && p < 0 && o < 0))
                throw new InvalidDataException("Example predictions need example_id plus score, predicted_label or output.");
            var known = dataset.Examples.Select(x => x.Id).ToHashSet();
            int unknown = 0;
            foreach (var r in table.Rows)
            {
                if (r[id] is not { } eid || !known.Contains(eid)) { unknown++; continue; }
                if (result.Examples.Any(x => x.ExampleId == eid)) throw new InvalidDataException($"Duplicate prediction for example '{eid}'.");
                if (Num(r, s) is null && (p < 0 || string.IsNullOrWhiteSpace(r[p])) && (o < 0 || string.IsNullOrWhiteSpace(r[o])))
                    throw new InvalidDataException($"Example '{eid}' has no prediction.");
                result.Examples.Add(new ExampleResult
                {
                    ExampleId = eid,
                    Score = Num(r, s),
                    PredictedLabel = p >= 0 ? r[p] : null,
                    Output = o >= 0 ? r[o] : null,
                    LatencyMs = Num(r, latency)
                });
            }
            if (unknown > 0) result.Warnings.Add($"{unknown} prediction rows reference unknown examples and were ignored.");
            int missing = dataset.Examples.Count - result.Examples.Count;
            if (missing > 0) { result.Warnings.Add($"{missing} dataset examples have no prediction (counted as failed)."); result.FailedItems = missing; }
        }
        if (result.Warnings.Count > 0) result.Status = RunStatus.CompletedWithErrors;
        var lat = result.Queries.Select(x => x.LatencyMs).Concat(result.Examples.Select(x => x.LatencyMs)).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (lat.Count > 0) result.Performance.MeanLatencyMs = lat.Average();
        return result;
    }
}
