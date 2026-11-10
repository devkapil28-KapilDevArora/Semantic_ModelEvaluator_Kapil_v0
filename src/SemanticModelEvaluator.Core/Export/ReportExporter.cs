using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SemanticModelEvaluator.Core.Comparison;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Persistence;

namespace SemanticModelEvaluator.Core.Export;

public sealed class ExportOptions
{
    /// <summary>Off by default. When on, query/document/example text from the dataset is included.</summary>
    public bool IncludeExampleText { get; set; }
    public int MaxExamples { get; set; } = 20;
}

public sealed class ExportContext
{
    public required Workspace Workspace { get; init; }
    public EvalDataset? Dataset { get; init; }
    public required IReadOnlyList<ScenarioEvaluation> Evaluations { get; init; }
    public required ComparisonTable Comparison { get; init; }
    public RankingResult? Ranking { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public static class ReportExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<string> StandardLimitations(ExportContext c)
    {
        var l = new List<string>();
        var ds = c.Dataset;
        if (ds is not null)
        {
            if (ds.Task is Models.EvalTask.Retrieval && ds.JudgmentCoverage < 1) l.Add($"Only {ds.JudgmentCoverage:P0} of queries have relevance judgments; quality metrics use judged queries only.");
            if (ds.IsSynthetic) l.Add("The dataset is synthetic and intended for verification, not for model selection.");
            if (!ds.HasSafetyJudgments) l.Add("No safety judgments: no harm metrics are reported and no model is implied to be safe.");
        }
        else l.Add("Dataset unavailable at export time; dataset statistics are omitted.");
        if (c.Evaluations.Any(e => e.Result.IsSynthetic)) l.Add("Includes deterministic synthetic baselines; their numbers are not real model performance.");
        if (c.Evaluations.Any(e => !e.Result.Performance.Measured)) l.Add("Performance metrics are unavailable for imported predictions.");
        if (c.Evaluations.Select(e => e.Scenario.Threshold).Distinct().Count() > 1) l.Add("Scenarios use different thresholds; score scales differ between models.");
        l.Add("Unjudged retrieved documents are counted as non-relevant; see Judged@K for coverage.");
        l.AddRange(c.Limitations);
        return l;
    }

    public static string ToCsv(ExportContext c)
    {
        DatasetPolicy.Ensure(c.Workspace.Dataset?.Provenance, DatasetOperation.ExportSummary);
        var sb = new StringBuilder();
        sb.AppendLine("scenario,model_key,run_id,dataset,dataset_version,dataset_hash,threshold,k,metric_id,metric,value,delta_vs_reference,assessment,direction,samples,judged,missing,is_reference,synthetic");
        var byId = c.Evaluations.ToDictionary(e => e.Scenario.Id);
        foreach (var row in c.Comparison.Rows)
            foreach (var cell in row.Cells)
            {
                var e = byId[cell.ScenarioId];
                sb.AppendLine(string.Join(",", new[]
                {
                    e.Scenario.Name, e.Scenario.ModelKey, e.Scenario.RunId, c.Workspace.Dataset?.Name ?? "", c.Workspace.Dataset?.Version ?? "", c.Workspace.Dataset?.ContentHash ?? "",
                    F(e.Scenario.Threshold), e.Scenario.K.ToString(Inv), row.Metric.Id, row.Metric.Name, F(cell.Value), F(cell.Delta), cell.Assessment.ToString(),
                    row.Metric.Direction.ToString(), cell.SampleCount.ToString(Inv), cell.JudgedCount.ToString(Inv), cell.MissingCount.ToString(Inv),
                    (cell.ScenarioId == c.Comparison.ReferenceScenarioId).ToString(), e.Result.IsSynthetic.ToString()
                }.Select(Csv)));
            }
        return sb.ToString();
    }

    public static string ToJson(ExportContext c, ExportOptions o)
    {
        DatasetPolicy.Ensure(c.Workspace.Dataset?.Provenance, o.IncludeExampleText ? DatasetOperation.ExportSamples : DatasetOperation.ExportSummary);
        var refScenario = c.Evaluations.First(e => e.Scenario.Id == c.Comparison.ReferenceScenarioId).Scenario;
        var summary = new
        {
            application = AppPaths.AppName,
            exportedAt = DateTimeOffset.Now,
            workspace = c.Workspace.Name,
            dataset = c.Workspace.Dataset,
            datasetStats = c.Dataset is null ? null : new
            {
                task = c.Dataset.Task.ToString(),
                queries = c.Dataset.Queries.Count,
                documents = c.Dataset.Documents.Count,
                examples = c.Dataset.Examples.Count,
                judgments = c.Dataset.Judgments.Count,
                judgmentCoverage = c.Dataset.JudgmentCoverage,
                classification = c.Dataset.DataClassification
            },
            reference = new { refScenario.Id, refScenario.Name, refScenario.ModelKey },
            scenarios = c.Evaluations.Select(e => new
            {
                e.Scenario.Id,
                e.Scenario.Name,
                e.Scenario.ModelKey,
                e.Scenario.RunId,
                e.Scenario.Threshold,
                e.Scenario.K,
                e.Scenario.Notes,
                model = c.Workspace.Models.FirstOrDefault(m => m.Identity.Key == e.Scenario.ModelKey),
                synthetic = e.Result.IsSynthetic,
                status = e.Result.Status.ToString(),
                performance = e.Result.Performance,
                environment = c.Workspace.Runs.FirstOrDefault(r => r.RunId == e.Scenario.RunId)?.Environment,
                configuration = c.Workspace.Runs.FirstOrDefault(r => r.RunId == e.Scenario.RunId)?.Config,
                metrics = e.Snapshot.Values.Values
            }),
            comparison = c.Comparison.Rows.Select(r => new
            {
                metric = r.Metric.Id,
                r.Metric.Name,
                direction = r.Metric.Direction.ToString(),
                r.Metric.Explanation,
                r.Metric.MissingDataPolicy,
                cells = r.Cells
            }),
            ranking = c.Ranking is null ? null : new
            {
                policy = c.Ranking.Policy.Name,
                c.Ranking.Formula,
                c.Ranking.MissingDataPolicy,
                weights = c.Ranking.Policy.Weights,
                constraints = c.Ranking.Policy.Constraints,
                rows = c.Ranking.Rows.Select(r => new { scenario = r.Scenario.Name, r.Rank, r.Score, r.ExclusionReason }),
                c.Ranking.Notes
            },
            limitations = StandardLimitations(c),
            examples = o.IncludeExampleText ? Examples(c, o) : null,
            exampleTextIncluded = o.IncludeExampleText
        };
        return JsonSerializer.Serialize(summary, WorkspaceSerializer.Options);
    }

    private static List<object> Examples(ExportContext c, ExportOptions o)
    {
        var list = new List<object>();
        if (c.Dataset is null) return list;
        foreach (var q in c.Dataset.Queries.Take(o.MaxExamples))
            list.Add(new
            {
                q.Id,
                q.Text,
                results = c.Evaluations.Select(e => new
                {
                    scenario = e.Scenario.Name,
                    top = e.Result.Queries.FirstOrDefault(r => r.QueryId == q.Id)?.Results
                        .Where(d => e.Scenario.Threshold is null || d.Score >= e.Scenario.Threshold).Take(Math.Min(e.Scenario.K, 5))
                        .Select(d => new { d.DocumentId, d.Score, text = c.Dataset.FindDocument(d.DocumentId)?.Text })
                })
            });
        foreach (var x in c.Dataset.Examples.Take(o.MaxExamples))
            list.Add(new { x.Id, x.Text, x.Text2, x.Label, predictions = c.Evaluations.Select(e => new { scenario = e.Scenario.Name, prediction = e.Result.Examples.FirstOrDefault(r => r.ExampleId == x.Id) }) });
        return list;
    }

    public static string ToText(ExportContext c)
    {
        DatasetPolicy.Ensure(c.Workspace.Dataset?.Provenance, DatasetOperation.ExportSummary);
        var sb = new StringBuilder();
        var reference = c.Evaluations.First(e => e.Scenario.Id == c.Comparison.ReferenceScenarioId);
        sb.AppendLine($"{AppPaths.AppName} — comparison summary");
        sb.AppendLine($"Workspace: {c.Workspace.Name}    Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        if (c.Workspace.Dataset is { } d) sb.AppendLine($"Dataset: {d.Name} v{d.Version} ({d.Task}), hash {Short(d.ContentHash)}");
        sb.AppendLine($"Reference: {reference.Scenario.Name}");
        sb.AppendLine();
        foreach (var e in c.Evaluations)
            sb.AppendLine($"• {e.Scenario.Name}: {e.Scenario.ModelKey}, threshold {F(e.Scenario.Threshold, "none")}, K={e.Scenario.K}{(e.Result.IsSynthetic ? " [synthetic]" : "")}");
        sb.AppendLine();
        foreach (var row in c.Comparison.Rows)
        {
            sb.Append($"{row.Metric.Name} ({row.Metric.DirectionText}): ");
            sb.AppendLine(string.Join("; ", row.Cells.Select(cell =>
            {
                var name = c.Evaluations.First(e => e.Scenario.Id == cell.ScenarioId).Scenario.Name;
                var val = cell.Value is null ? "n/a" : cell.Value.Value.ToString(row.Metric.Format, Inv);
                return cell.Assessment == DeltaAssessment.Reference || cell.Delta is null ? $"{name} {val}" : $"{name} {val} (Δ {cell.Delta:+0.000;-0.000;0}, {cell.Assessment})";
            })));
        }
        if (c.Ranking is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Ranking policy: {c.Ranking.Policy.Name}. {c.Ranking.Formula}");
            foreach (var r in c.Ranking.Rows) sb.AppendLine(r.Rank is null ? $"  – {r.Scenario.Name}: excluded ({r.ExclusionReason})" : $"  {r.Rank}. {r.Scenario.Name}: {r.Score:0.000}");
            sb.AppendLine("  " + c.Ranking.MissingDataPolicy);
        }
        sb.AppendLine();
        sb.AppendLine("Limitations:");
        foreach (var l in StandardLimitations(c)) sb.AppendLine("  - " + l);
        return sb.ToString();
    }

    public static string ToHtml(ExportContext c, ExportOptions o)
    {
        DatasetPolicy.Ensure(c.Workspace.Dataset?.Provenance, o.IncludeExampleText ? DatasetOperation.ExportSamples : DatasetOperation.ExportSummary);
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        var byId = c.Evaluations.ToDictionary(e => e.Scenario.Id);
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(c.Workspace.Name)).Append(" — ").Append(AppPaths.AppName).Append("</title>");
        sb.Append("<style>body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1b1b1b}table{border-collapse:collapse;margin:12px 0}th,td{border:1px solid #d1d1d1;padding:6px 10px;text-align:right}th:first-child,td:first-child{text-align:left}th{background:#f3f3f3}.b{color:#0e700e}.w{color:#b10e1c}.ref{background:#eaf3fb}.muted{color:#616161}h1{font-weight:600}.chip{display:inline-block;padding:2px 10px;border-radius:12px;color:#fff;margin-right:6px}</style></head><body>");
        sb.Append("<h1>").Append(E(AppPaths.AppName)).Append(" — comparison report</h1>");
        sb.Append("<p class=muted>Workspace <b>").Append(E(c.Workspace.Name)).Append("</b> · exported ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", Inv)).Append("</p>");
        if (c.Workspace.Dataset is { } d)
            sb.Append("<h2>Dataset</h2><p>").Append(E($"{d.Name} v{d.Version} · task {d.Task} · content hash {Short(d.ContentHash)}"))
              .Append(c.Dataset is null ? "" : E($" · {c.Dataset.Queries.Count} queries, {c.Dataset.Documents.Count} documents, {c.Dataset.Examples.Count} examples, {c.Dataset.Judgments.Count} judgments, judgment coverage {c.Dataset.JudgmentCoverage:P0}, classification {c.Dataset.DataClassification}")).Append("</p>");
        sb.Append("<h2>Scenarios</h2><table><tr><th>Scenario</th><th>Model identity</th><th>Run</th><th>Threshold</th><th>K</th><th>Runtime</th><th>Notes</th></tr>");
        foreach (var e in c.Evaluations)
            sb.Append("<tr").Append(e.Scenario.Id == c.Comparison.ReferenceScenarioId ? " class=ref" : "").Append("><td><span class=chip style=\"background:").Append(E(e.Scenario.Color)).Append("\">&nbsp;</span>")
              .Append(E(e.Scenario.Name)).Append(e.Scenario.Id == c.Comparison.ReferenceScenarioId ? " (reference)" : "").Append(e.Result.IsSynthetic ? " [synthetic]" : "")
              .Append("</td><td>").Append(E(e.Scenario.ModelKey)).Append("</td><td>").Append(E(e.Scenario.RunId)).Append("</td><td>").Append(F(e.Scenario.Threshold, "none"))
              .Append("</td><td>").Append(e.Scenario.K).Append("</td><td>").Append(E($"{e.Result.Performance.Runtime} / {e.Result.Performance.Device}")).Append("</td><td>").Append(E(e.Scenario.Notes)).Append("</td></tr>");
        sb.Append("</table>");
        sb.Append("<h2>Metrics</h2><table><tr><th>Metric</th><th>Direction</th>");
        foreach (var e in c.Evaluations) sb.Append("<th>").Append(E(e.Scenario.Name)).Append("</th>");
        sb.Append("</tr>");
        foreach (var row in c.Comparison.Rows)
        {
            sb.Append("<tr><td title=\"").Append(E(row.Metric.Explanation)).Append("\">").Append(E(row.Metric.Name)).Append("</td><td>").Append(E(row.Metric.DirectionText)).Append("</td>");
            foreach (var cell in row.Cells)
            {
                var cls = cell.Assessment switch { DeltaAssessment.Better => "b", DeltaAssessment.Worse => "w", DeltaAssessment.Reference => "ref", _ => "" };
                sb.Append("<td class=\"").Append(cls).Append("\">").Append(cell.Value is null ? "n/a" : cell.Value.Value.ToString(row.Metric.Format, Inv));
                if (cell.Delta is { } dd && cell.Assessment != DeltaAssessment.Reference) sb.Append("<br><small>Δ ").Append(dd.ToString("+0.000;-0.000;0", Inv)).Append("</small>");
                sb.Append("<br><small class=muted>n=").Append(cell.SampleCount).Append(cell.MissingCount > 0 ? $", missing {cell.MissingCount}" : "").Append("</small></td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
        if (c.Ranking is not null)
        {
            sb.Append("<h2>Ranking — ").Append(E(c.Ranking.Policy.Name)).Append("</h2><p><code>").Append(E(c.Ranking.Formula)).Append("</code></p><p class=muted>").Append(E(c.Ranking.MissingDataPolicy)).Append("</p><table><tr><th>Scenario</th><th>Rank</th><th>Score</th><th>Excluded because</th></tr>");
            foreach (var r in c.Ranking.Rows)
                sb.Append("<tr><td>").Append(E(r.Scenario.Name)).Append("</td><td>").Append(r.Rank?.ToString(Inv) ?? "–").Append("</td><td>").Append(r.Score?.ToString("0.000", Inv) ?? "–").Append("</td><td>").Append(E(r.ExclusionReason)).Append("</td></tr>");
            sb.Append("</table>");
            foreach (var n in c.Ranking.Notes) sb.Append("<p class=muted>").Append(E(n)).Append("</p>");
        }
        sb.Append("<h2>Metric definitions</h2><ul>");
        foreach (var row in c.Comparison.Rows) sb.Append("<li><b>").Append(E(row.Metric.Name)).Append("</b> (v").Append(E(row.Metric.Version)).Append("): ").Append(E(row.Metric.Explanation)).Append(" <i>Missing data: ").Append(E(row.Metric.MissingDataPolicy)).Append("</i></li>");
        sb.Append("</ul><h2>Environment</h2><ul>");
        foreach (var r in c.Workspace.Runs.Where(r => c.Evaluations.Any(e => e.Scenario.RunId == r.RunId)))
            sb.Append("<li>").Append(E($"Run {r.RunId}: {r.Environment}; batch {r.Config.BatchSize}, concurrency {r.Config.MaxConcurrency}, retain depth {r.Config.RetainDepth}")).Append("</li>");
        sb.Append("</ul><h2>Limitations</h2><ul>");
        foreach (var l in StandardLimitations(c)) sb.Append("<li>").Append(E(l)).Append("</li>");
        sb.Append("</ul>");
        if (o.IncludeExampleText && c.Dataset is not null)
        {
            sb.Append("<h2>Examples (sensitive)</h2><table><tr><th>Query</th>");
            foreach (var e in c.Evaluations) sb.Append("<th>").Append(E(e.Scenario.Name)).Append("</th>");
            sb.Append("</tr>");
            foreach (var q in c.Dataset.Queries.Take(o.MaxExamples))
            {
                sb.Append("<tr><td>").Append(E(q.Text)).Append("</td>");
                foreach (var e in c.Evaluations)
                {
                    var top = e.Result.Queries.FirstOrDefault(r => r.QueryId == q.Id)?.Results.FirstOrDefault(d => e.Scenario.Threshold is null || d.Score >= e.Scenario.Threshold);
                    sb.Append("<td>").Append(top is null ? "<i>no result</i>" : E(Trunc(c.Dataset.FindDocument(top.DocumentId)?.Text ?? top.DocumentId, 120)) + $" <small>({top.Score:0.000})</small>").Append("</td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }
        else sb.Append("<p class=muted>Example text was excluded from this export.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    private static string Short(string h) => h.Length > 12 ? h[..12] : h;
    private static string F(double? v, string none = "") => v is null ? none : v.Value.ToString("0.######", Inv);
    private static string Csv(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
