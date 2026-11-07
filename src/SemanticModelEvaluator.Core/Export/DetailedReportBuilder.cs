using System.Globalization;
using System.Net;
using System.Text;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Pairing;
using SemanticModelEvaluator.Core.Security;

namespace SemanticModelEvaluator.Core.Export;

public sealed class DetailedReportOptions
{
    /// <summary>Include query/example text, labels and thumbnails. Requires <see cref="SensitiveContentConfirmed"/> for sensitive data.</summary>
    public bool IncludeSampleContent { get; init; }
    /// <summary>The user explicitly confirmed exporting sample-level content from a sensitive or unclassified source.</summary>
    public bool SensitiveContentConfirmed { get; init; }
    public string ApplicationVersion { get; init; } = "unknown";
    public int MaxRows { get; init; } = 500;
    public int MaxThumbnails { get; init; } = 40;
    public string? Notes { get; init; }
}

/// <summary>
/// Builds a single, self-contained, offline HTML assessment of a two-model comparison. Every model- or dataset-supplied value is
/// redacted and HTML-encoded; no external resources are referenced (a CSP forbids them); unknown values are printed as "unknown", never 0.
/// </summary>
public static partial class DetailedReportBuilder
{
    [System.Text.RegularExpressions.GeneratedRegex("'[^'\\r\\n]{1,200}'|\"[^\"\\r\\n]{1,200}\"")]
    private static partial System.Text.RegularExpressions.Regex QuotedIdentifier();
    [System.Text.RegularExpressions.GeneratedRegex(@"[^\s,;:()\[\]{}<>]+")]
    private static partial System.Text.RegularExpressions.Regex IdToken();

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Conservative: anything not explicitly public, non-sensitive or synthetic requires confirmation before sample-level export.</summary>
    public static bool IsSensitive(string? classification)
    {
        if (string.IsNullOrWhiteSpace(classification)) return true;
        var c = classification.Trim().ToLowerInvariant();
        if (c.StartsWith("sensitive", StringComparison.Ordinal) || c.Contains(" sensitive") || c.Contains("confidential") || c.Contains("restricted") || c.Contains("highly")) return true;
        return !(c.StartsWith("non-sensitive", StringComparison.Ordinal) || c.StartsWith("nonsensitive", StringComparison.Ordinal)
            || c.StartsWith("public", StringComparison.Ordinal) || c.StartsWith("synthetic", StringComparison.Ordinal));
    }

    public static bool IsSensitive(PairComparisonResult r) =>
        IsSensitive(r.DatasetClassification) || (r.A.Classification is { } ca && IsSensitive(ca)) || (r.B.Classification is { } cb && IsSensitive(cb));

    private static string H(string? s) => WebUtility.HtmlEncode(Redactor.Redact(s ?? ""));
    private static string F(double? v, string format = "0.000") => v is { } x ? x.ToString(format, Inv) : "unknown";
    private static string D(double? v, string format = "0.000") => v is { } x ? (x > 0 ? "+" : "") + x.ToString(format, Inv) : "unknown";

    public static string Build(PairComparisonResult r, DetailedReportOptions o)
    {
        // SVG coordinates and every interpolated number must use '.' decimals regardless of the user's locale.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = Inv;
        try { return BuildCore(r, o); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static string BuildCore(PairComparisonResult r, DetailedReportOptions o)
    {
        var sensitive = IsSensitive(r);
        if (o.IncludeSampleContent && sensitive && !o.SensitiveContentConfirmed)
            throw new InvalidOperationException("The dataset is sensitive or unclassified. Confirm sample-level export before including query text, labels or thumbnails.");
        bool content = o.IncludeSampleContent;
        // Aggregate-only sensitive reports must not reveal item/document identifiers inside free-form warnings: mask quoted text and any
        // token that equals a known query, example or document id.
        HashSet<string>? knownIds = sensitive && !content
            ? r.Rows.Select(x => x.Id).Concat(r.Rows.SelectMany(x => x.A.Retained.Concat(x.B.Retained)).Select(d => d.DocumentId))
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToHashSet(StringComparer.Ordinal)
            : null;
        string ItemSafe(string s)
        {
            if (knownIds is null) return s;
            s = QuotedIdentifier().Replace(s, m => m.Value[0] + "[item]" + m.Value[0]);
            return IdToken().Replace(s, m => knownIds.Contains(m.Value) || knownIds.Contains(m.Value.TrimEnd('.')) ? "[item]" + (m.Value.EndsWith('.') && !knownIds.Contains(m.Value) ? "." : "") : m.Value);
        }
        var sb = new StringBuilder(64 * 1024);
        var a = r.A.Configuration; var b = r.B.Configuration;

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<title>Model comparison: ").Append(H(a.DisplayName)).Append(" vs ").Append(H(b.DisplayName)).Append("</title>\n")
          .Append(ThemeScript).Append("<style>").Append(Css).Append("</style>\n</head>\n<body>\n");
        sb.Append("<header class=\"top\"><p class=\"eyebrow\">Semantic Model Evaluator — detailed assessment</p><h1>")
          .Append(H(a.DisplayName)).Append(" <span class=\"vs\">vs</span> ").Append(H(b.DisplayName)).Append("</h1>")
          .Append("<p class=\"muted\">Dataset ").Append(H(r.Dataset.Name)).Append(" v").Append(H(r.Dataset.Version)).Append(" · generated ")
          .Append(H(r.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv))).Append("</p>");
        sb.Append("<p class=\"notice ").Append(sensitive ? "warn" : "info").Append("\" role=\"note\"><strong>Data handling:</strong> ")
          .Append(H(string.IsNullOrWhiteSpace(r.DatasetClassification) ? "Unclassified" : r.DatasetClassification)).Append(". ")
          .Append(content ? (sensitive ? "Sample-level content is included after explicit confirmation — handle this file under the source's rules." : "Sample-level content is included.")
                          : sensitive ? "Sample-level content (query text, labels, images, item and document identifiers, per-item scores) is omitted; aggregate results only."
                          : "Sample-level content (query text, labels, images) is omitted; identifiers and scores only.")
          .Append("</p></header>\n<nav class=\"toc\" aria-label=\"Contents\"><ol>");
        string[] titles = ["Executive summary", "Evaluation objective", "Model A configuration", "Model B configuration", "Provider and version information",
            "Dataset and exact version", "Data-source provenance", "Evaluation configuration", "Summary metrics and deltas", "Quality assessment",
            "Safety and harm assessment", "Empty-query, failure and unjudged analysis", "Threshold analysis", "Visual comparisons",
            "Query-level findings", "Strengths and weaknesses", "Evidence-based recommendation", "Limitations and missing data",
            "Reproducibility", "Generation timestamp", "Application version"];
        for (int i = 0; i < titles.Length; i++) sb.Append("<li><a href=\"#s").Append(i + 1).Append("\">").Append(titles[i]).Append("</a></li>");
        sb.Append("</ol></nav>\n<main>\n");

        void Section(int n, Action body)
        {
            sb.Append("<section id=\"s").Append(n).Append("\" aria-labelledby=\"h").Append(n).Append("\"><h2 id=\"h").Append(n).Append("\">")
              .Append(n).Append(". ").Append(titles[n - 1]).Append("</h2>\n");
            body();
            sb.Append("</section>\n");
        }

        Section(1, () =>
        {
            sb.Append("<p class=\"headline\">").Append(H(r.Recommendation.Headline)).Append("</p>");
            sb.Append("<p>").Append(H(r.Recommendation.Detail)).Append("</p>");
            sb.Append("<p><strong>Confidence:</strong> <span class=\"chip\">").Append(r.Recommendation.Confidence).Append("</span> ").Append(H(r.Recommendation.ConfidenceReason)).Append("</p>");
            if (!r.IsComparable) sb.Append("<p class=\"notice bad\" role=\"alert\"><strong>Not comparable.</strong> ").Append(H(ItemSafe(string.Join(" ", r.Blocking)))).Append("</p>");
            else if (!r.IsLikeForLike) sb.Append("<p class=\"notice warn\" role=\"alert\"><strong>Not fully like-for-like:</strong> ").Append(H(ItemSafe(string.Join(" ", r.Warnings)))).Append("</p>");
            else sb.Append("<p class=\"notice info\">Eligibility checks passed: same dataset identity and task on both sides.</p>");
            sb.Append("<p class=\"muted\">This is evidence for one dataset version and configuration; it does not show that either model is better in general.</p>");
        });
        Section(2, () => sb.Append("<p>").Append(H(string.IsNullOrWhiteSpace(r.Settings.Objective)
            ? $"Compare {a.DisplayName} and {b.DisplayName} on {r.Dataset.Name} v{r.Dataset.Version} ({r.Dataset.Task}) to decide which configuration better meets quality and safety needs."
            : r.Settings.Objective)).Append("</p>"));
        Section(3, () => ConfigTable(sb, r.A));
        Section(4, () => ConfigTable(sb, r.B));
        Section(5, () =>
        {
            sb.Append("<table><caption>Provider and version</caption><thead><tr><th scope=\"col\">Field</th><th scope=\"col\">Model A</th><th scope=\"col\">Model B</th></tr></thead><tbody>");
            Row3(sb, "Provider", a.ProviderName, b.ProviderName);
            Row3(sb, "Provider id", a.ProviderId, b.ProviderId);
            Row3(sb, "Model", a.ModelId, b.ModelId);
            Row3(sb, "Version / revision", a.Revision ?? "unknown", b.Revision ?? "unknown");
            Row3(sb, "Endpoint / deployment", a.Endpoint ?? "n/a (local or captured)", b.Endpoint ?? "n/a (local or captured)");
            Row3(sb, "Runtime", r.A.Result.Performance.Runtime, r.B.Result.Performance.Runtime);
            Row3(sb, "Execution", r.A.Result.Execution.ToString(), r.B.Result.Execution.ToString());
            sb.Append("</tbody></table>");
        });
        Section(6, () =>
        {
            sb.Append("<dl>");
            Dl(sb, "Name", r.Dataset.Name); Dl(sb, "Identifier", r.Dataset.Id); Dl(sb, "Exact version", r.Dataset.Version);
            Dl(sb, "Payload / content hash", string.IsNullOrEmpty(r.Dataset.ContentHash) ? "unknown" : r.Dataset.ContentHash);
            Dl(sb, "Task", r.Dataset.Task.ToString()); Dl(sb, "Classification", r.DatasetClassification);
            Dl(sb, "Items compared", r.Rows.Count.ToString(Inv));
            sb.Append("</dl>");
        });
        Section(7, () =>
        {
            sb.Append("<table><caption>Where each result came from</caption><thead><tr><th scope=\"col\">Field</th><th scope=\"col\">Model A</th><th scope=\"col\">Model B</th></tr></thead><tbody>");
            Row3(sb, "Origin", Origin(r.A.Origin), Origin(r.B.Origin));
            Row3(sb, "Source", r.A.SourceDescription, r.B.SourceDescription);
            Row3(sb, "Run id", r.A.RunId ?? "unknown", r.B.RunId ?? "unknown");
            Row3(sb, "Handling classification", r.A.Classification ?? r.DatasetClassification, r.B.Classification ?? r.DatasetClassification);
            Row3(sb, "Run timestamp", r.A.RunAt?.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv) ?? "unknown", r.B.RunAt?.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv) ?? "unknown");
            Row3(sb, "Run dataset identity", $"{r.A.RunDataset.Name} v{r.A.RunDataset.Version} {Short(r.A.RunDataset.ContentHash)}", $"{r.B.RunDataset.Name} v{r.B.RunDataset.Version} {Short(r.B.RunDataset.ContentHash)}");
            sb.Append("</tbody></table><p class=\"muted\">Links to storage are intentionally omitted. Changing the threshold recalculates metrics from captured scores; it never re-runs or retrains a model.</p>");
        });
        Section(8, () =>
        {
            sb.Append("<dl>");
            Dl(sb, "K (results considered per query)", r.Settings.K.ToString(Inv));
            Dl(sb, "Score threshold, Model A", r.A.Threshold is { } ta ? ta.ToString("0.####", Inv) : "none (all cached results)");
            Dl(sb, "Score threshold, Model B", r.B.Threshold is { } tb ? tb.ToString("0.####", Inv) : "none (all cached results)");
            Dl(sb, "Cached depth, A / B", $"{r.A.CachedDepth?.ToString(Inv) ?? "unknown"} / {r.B.CachedDepth?.ToString(Inv) ?? "unknown"}");
            Dl(sb, "Sample", r.Settings.SampledIds is { } ids ? $"{ids.Count} item(s), deterministic sample" : "Full dataset");
            Dl(sb, "Inclusion rule", r.Settings.JudgedItemsOnly ? "Judged items only (unjudged excluded)" : "All items (unjudged reported as unknown)");
            Dl(sb, "Evaluation dimensions", r.Settings.Dimensions is { } dims ? string.Join(", ", dims.Order()) : "All supported (quality, harm, coverage, distribution)");
            Dl(sb, "Tie margin", PairComparer.TieMargin.ToString(Inv));
            sb.Append("</dl><h3>Eligibility rules</h3><ul>");
            foreach (var rule in PairComparer.EligibilityRules) sb.Append("<li>").Append(H(rule)).Append("</li>");
            sb.Append("</ul>");
        });
        Section(9, () =>
        {
            MetricTable(sb, r.Metrics, "All comparable metrics");
            sb.Append("<table><caption>Latency, errors and usage</caption><thead><tr><th scope=\"col\">Measure</th><th scope=\"col\">Model A</th><th scope=\"col\">Model B</th><th scope=\"col\">Δ (A − B)</th><th scope=\"col\">Basis</th></tr></thead><tbody>");
            foreach (var p in r.Performance)
                sb.Append("<tr><th scope=\"row\">").Append(H(p.Name)).Append("</th><td>").Append(F(p.A, p.Format)).Append("</td><td>").Append(F(p.B, p.Format))
                  .Append("</td><td>").Append(D(p.Delta, p.Format)).Append("</td><td>").Append(H(p.Basis)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        });
        Section(10, () =>
        {
            var q = r.Metrics.Where(m => m.Definition.Category == MetricCategory.Quality).ToList();
            if (q.Count == 0) { sb.Append("<p>No quality metric could be computed (judgments or labels are missing).</p>"); return; }
            MetricTable(sb, q, "Quality metrics");
            sb.Append("<p>Per-item primary quality (").Append(H(r.PrimaryMetricId ?? (r.IsRetrieval ? "nDCG@K" : "correctness"))).Append("): A better on <strong>").Append(r.AWins)
              .Append("</strong>, B better on <strong>").Append(r.BWins).Append("</strong>, ties ").Append(r.Ties).Append(", unknown ").Append(r.Unknown)
              .Append(r.Recommendation.SignTestP is { } p ? $" (two-sided sign test p = {p.ToString("0.0000", Inv)})" : "").Append(".</p>");
        });
        Section(11, () =>
        {
            var h = r.Metrics.Where(m => m.Definition.Category == MetricCategory.Harm).ToList();
            if (h.Count == 0) { sb.Append("<p>No harm metric is available: the dataset has no safety judgments. Unjudged results are never treated as safe.</p>"); }
            else MetricTable(sb, h, "Harm metrics — lower is better; values are the actual measured rates (not inverted)");
            sb.Append("<p>Items where at least one model retained a result judged harmful: <strong>").Append(r.Rows.Count(x => x.Flags.HasFlag(QueryFlags.SafetyConcern))).Append("</strong>.</p>");
        });
        Section(12, () =>
        {
            sb.Append("<table><caption>Coverage and failures</caption><thead><tr><th scope=\"col\">Count</th><th scope=\"col\">Model A</th><th scope=\"col\">Model B</th></tr></thead><tbody>");
            Row3(sb, "Items in scope", r.CountsA.Total.ToString(Inv), r.CountsB.Total.ToString(Inv));
            Row3(sb, "Judged items", r.CountsA.Judged.ToString(Inv), r.CountsB.Judged.ToString(Inv));
            Row3(sb, "Unjudged items (unknown)", r.CountsA.Unjudged.ToString(Inv), r.CountsB.Unjudged.ToString(Inv));
            Row3(sb, "Evaluated (no error)", r.CountsA.Evaluated.ToString(Inv), r.CountsB.Evaluated.ToString(Inv));
            if (r.IsRetrieval)
            {
                Row3(sb, "Retained results", r.CountsA.Retained.ToString(Inv), r.CountsB.Retained.ToString(Inv));
                Row3(sb, "Filtered by threshold", r.CountsA.Filtered.ToString(Inv), r.CountsB.Filtered.ToString(Inv));
                Row3(sb, "Empty queries", r.CountsA.Empty.ToString(Inv), r.CountsB.Empty.ToString(Inv));
            }
            Row3(sb, "Errors (model failed on the item)", r.CountsA.Errors.ToString(Inv), r.CountsB.Errors.ToString(Inv));
            Row3(sb, "Missing from run", r.CountsA.Missing.ToString(Inv), r.CountsB.Missing.ToString(Inv));
            sb.Append("</tbody></table><p class=\"muted\">Failed, missing and unjudged items are not scored as zero; they are excluded from quality metrics and reported here.</p>");
        });
        Section(13, () =>
        {
            if (r.CurveA is null || r.CurveB is null) { sb.Append("<p>Threshold analysis is not available (no comparable score data or the runs are not comparable).</p>"); return; }
            sb.Append("<p>Each model is swept over its own score range because providers use different score scales. Curves are recalculated from captured scores. Active thresholds (vertical markers): A ")
              .Append(F(r.A.Threshold, "0.0000").Replace("unknown", "none")).Append(", B ").Append(F(r.B.Threshold, "0.0000").Replace("unknown", "none")).Append(".</p>");
            foreach (var (side, label) in new[] { (r.A, "A"), (r.B, "B") })
                if (side.Result.ScoresIncludeRanks)
                    sb.Append("<p class=\"notice warn\" role=\"note\">Model ").Append(label).Append(" has results with positions but no score; −rank is used for them, so its threshold acts as a rank cut-off there and is not comparable to a score threshold.</p>");
            sb.Append("<div class=\"grid2\">");
            foreach (var id in r.CurveMetricIds)
            {
                var name = r.Metrics.FirstOrDefault(m => m.Definition.Id == id)?.Definition.Name ?? id;
                sb.Append("<figure>").Append(Svg.Lines(name, r.CurveA, r.CurveB, id, a.DisplayName, b.DisplayName, r.A.Threshold, r.B.Threshold)).Append("<figcaption>").Append(H(name)).Append(" vs threshold</figcaption>");
                sb.Append("<details><summary>Data table</summary><table><thead><tr><th scope=\"col\">Threshold A</th><th scope=\"col\">A</th><th scope=\"col\">Threshold B</th><th scope=\"col\">B</th></tr></thead><tbody>");
                for (int i = 0; i < Math.Max(r.CurveA.Thresholds.Length, r.CurveB.Thresholds.Length); i++)
                {
                    double? At(ThresholdCurve c, int j) => j < c.Thresholds.Length ? c.Thresholds[j] : null;
                    double? Val(ThresholdCurve c, int j) => j < c.Thresholds.Length && c.Series.TryGetValue(id, out var s) && j < s.Length ? s[j] : null;
                    sb.Append("<tr><td>").Append(F(At(r.CurveA, i), "0.0000")).Append("</td><td>").Append(F(Val(r.CurveA, i))).Append("</td><td>")
                      .Append(F(At(r.CurveB, i), "0.0000")).Append("</td><td>").Append(F(Val(r.CurveB, i))).Append("</td></tr>");
                }
                sb.Append("</tbody></table></details></figure>");
            }
            sb.Append("</div>");
        });
        Section(14, () =>
        {
            var bounded = r.Metrics.Where(m => m.Definition.IsBounded01 && m.A?.Value is not null && m.B?.Value is not null).ToList();
            if (bounded.Count == 0) { sb.Append("<p>No bounded metric is available on both sides.</p>"); return; }
            sb.Append("<figure>").Append(Svg.Bars(bounded, a.DisplayName, b.DisplayName)).Append("<figcaption>Side-by-side values on a fixed 0–1 scale. Exact values are in section 9.</figcaption></figure>");
            sb.Append("<h3>Metric-difference heatmap</h3><table class=\"heat\"><caption>Δ = A − B; ▲ favours A, ▼ favours B, ● tie, · no preferred direction</caption><thead><tr><th scope=\"col\">Metric</th><th scope=\"col\">Δ</th><th scope=\"col\">Favours</th></tr></thead><tbody>");
            foreach (var m in bounded)
            {
                var cls = m.Better switch { "A" => "fa", "B" => "fb", "Tie" => "ft", _ => "fn" };
                var mark = m.Better switch { "A" => "▲ A", "B" => "▼ B", "Tie" => "● tie", _ => "· n/a" };
                var strength = Math.Min(1, Math.Abs(m.Delta ?? 0) / 0.2);
                sb.Append("<tr><th scope=\"row\">").Append(H(m.Definition.Name)).Append("</th><td class=\"").Append(cls).Append("\" style=\"--s:")
                  .Append((0.15 + 0.85 * strength).ToString("0.00", Inv)).Append("\">").Append(D(m.Delta)).Append("</td><td>").Append(mark).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
            var radar = bounded.Where(m => m.Definition.Category is MetricCategory.Quality && m.Definition.Direction == Direction.HigherIsBetter).ToList();
            if (radar.Count >= 3)
                sb.Append("<figure>").Append(Svg.Radar(radar, a.DisplayName, b.DisplayName)).Append("<figcaption>Radar of quality metrics that share the same 0–1 scale where higher is better. Harm metrics are excluded rather than inverted.</figcaption></figure>");
            else sb.Append("<p class=\"muted\">Radar chart omitted: fewer than three quality metrics share a meaningful 0–1 scale.</p>");
        });
        Section(15, () => QueryTable(sb, r, o, content));
        Section(16, () =>
        {
            sb.Append("<div class=\"grid2\">");
            foreach (var (label, s, w) in new[] { ("Model A — " + a.DisplayName, r.StrengthsA, r.WeaknessesA), ("Model B — " + b.DisplayName, r.StrengthsB, r.WeaknessesB) })
            {
                sb.Append("<div class=\"card\"><h3>").Append(H(label)).Append("</h3><h4>Strengths</h4><ul>");
                foreach (var x in s.DefaultIfEmpty("None observed on this dataset.")) sb.Append("<li>").Append(H(x)).Append("</li>");
                sb.Append("</ul><h4>Weaknesses</h4><ul>");
                foreach (var x in w.DefaultIfEmpty("None observed on this dataset.")) sb.Append("<li>").Append(H(x)).Append("</li>");
                sb.Append("</ul></div>");
            }
            sb.Append("</div>");
        });
        Section(17, () =>
        {
            sb.Append("<p class=\"headline\">").Append(H(r.Recommendation.Headline)).Append("</p><p>").Append(H(r.Recommendation.Detail)).Append("</p>")
              .Append("<p>Confidence: <strong>").Append(r.Recommendation.Confidence).Append("</strong> — ").Append(H(r.Recommendation.ConfidenceReason)).Append("</p>")
              .Append("<p class=\"muted\">Neutral, evidence-based guidance for this dataset version and configuration. Validate on representative data before deployment decisions.</p>");
        });
        Section(18, () =>
        {
            sb.Append("<ul>");
            foreach (var l in r.Blocking.Concat(r.Warnings).Concat(r.Limitations).Select(ItemSafe).Distinct()) sb.Append("<li>").Append(H(l)).Append("</li>");
            sb.Append("</ul>");
        });
        Section(19, () =>
        {
            sb.Append("<dl>");
            Dl(sb, "Model A identity", r.A.Result.Model.Key + (r.A.Result.Model.ContentHash is { } ha ? " #" + Short(ha) : ""));
            Dl(sb, "Model B identity", r.B.Result.Model.Key + (r.B.Result.Model.ContentHash is { } hb ? " #" + Short(hb) : ""));
            Dl(sb, "Model A parameters", Params(a)); Dl(sb, "Model B parameters", Params(b));
            Dl(sb, "Dataset", $"{r.Dataset.Name} v{r.Dataset.Version} payload {r.Dataset.ContentHash}");
            Dl(sb, "K / thresholds", $"K={r.Settings.K}; A={r.A.Threshold?.ToString("0.####", Inv) ?? "none"}; B={r.B.Threshold?.ToString("0.####", Inv) ?? "none"}");
            Dl(sb, "Metric versions", string.Join(", ", r.Metrics.Select(m => $"{m.Definition.Id} v{m.Definition.Version}")));
            if (r.Settings.SampledIds is { } ids) Dl(sb, "Sampled ids (count)", ids.Count.ToString(Inv));
            Dl(sb, "Run ids", $"A {r.A.RunId ?? "unknown"}; B {r.B.RunId ?? "unknown"}");
            sb.Append("</dl><p class=\"muted\">Reload the same runs and dataset version in Semantic Model Evaluator and apply these settings to reproduce every number. No credentials, keys or storage URLs are recorded.</p>");
            if (!string.IsNullOrWhiteSpace(o.Notes)) sb.Append("<h3>Analyst notes</h3><p>").Append(H(o.Notes)).Append("</p>");
        });
        Section(20, () =>
        {
            var now = DateTimeOffset.Now;
            sb.Append("<p>Report generated: <time datetime=\"").Append(H(now.ToString("O", Inv))).Append("\">").Append(H(now.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv))).Append("</time></p>")
              .Append("<p>Comparison computed: <time datetime=\"").Append(H(r.GeneratedAt.ToString("O", Inv))).Append("\">").Append(H(r.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv))).Append("</time></p>");
        });
        Section(21, () =>
        {
            var plus = o.ApplicationVersion.IndexOf('+');
            sb.Append("<p>Semantic_ModelEvaluator_Kapil_V0 ").Append(H(plus > 0 ? o.ApplicationVersion[..plus] : o.ApplicationVersion)).Append("</p>")
              .Append("<p>Source commit: ").Append(plus > 0 ? "<code>" + H(o.ApplicationVersion[(plus + 1)..]) + "</code>" : "not recorded (built outside a git repository)").Append("</p>");
        });
        sb.Append("</main>\n<footer class=\"muted\">Self-contained report — no external resources. Values marked “unknown” were not available and were not treated as zero.</footer>\n")
          .Append(FilterScript).Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static string Origin(ResultOrigin o) => o switch
    {
        ResultOrigin.NewEvaluation => "New evaluation (model invoked by this application)",
        ResultOrigin.LoadedEvalKitLocal => "Loaded existing EvalKit run (local folder) — metrics recalculated from captured scores",
        ResultOrigin.LoadedEvalKitCloud => "Loaded existing EvalKit cloud result — metrics recalculated from captured scores",
        ResultOrigin.LoadedWorkspace => "Loaded saved workspace run",
        _ => "Imported predictions"
    };

    private static string Short(string? h) => string.IsNullOrEmpty(h) ? "unknown" : h.Length > 20 ? h[..20] + "…" : h;
    private static string Params(Pairing.ModelConfiguration c) => c.Parameters.Count == 0 ? "defaults" : string.Join("; ", c.Parameters.Select(p => $"{p.Key}={Redactor.RedactValue(p.Key, p.Value)}"));

    private static void ConfigTable(StringBuilder sb, PairSide s)
    {
        var c = s.Configuration;
        sb.Append("<dl>");
        Dl(sb, "Display name", c.DisplayName); Dl(sb, "Provider", c.ProviderName); Dl(sb, "Model", c.ModelId);
        Dl(sb, "Version / revision", c.Revision ?? "unknown"); Dl(sb, "Endpoint", c.Endpoint ?? "n/a");
        Dl(sb, "Parameters", Params(c)); Dl(sb, "Status", s.Result.Status.ToString());
        if (s.Result.IsSynthetic) Dl(sb, "Note", "Synthetic baseline — not real model performance");
        sb.Append("</dl>");
    }

    private static void Dl(StringBuilder sb, string k, string? v) => sb.Append("<dt>").Append(H(k)).Append("</dt><dd>").Append(H(string.IsNullOrEmpty(v) ? "unknown" : v)).Append("</dd>");
    private static void Row3(StringBuilder sb, string k, string? a, string? b) =>
        sb.Append("<tr><th scope=\"row\">").Append(H(k)).Append("</th><td>").Append(H(string.IsNullOrEmpty(a) ? "unknown" : a)).Append("</td><td>").Append(H(string.IsNullOrEmpty(b) ? "unknown" : b)).Append("</td></tr>");

    private static void MetricTable(StringBuilder sb, IEnumerable<PairMetricRow> rows, string caption)
    {
        sb.Append("<table><caption>").Append(H(caption)).Append("</caption><thead><tr><th scope=\"col\">Metric</th><th scope=\"col\">Model A</th><th scope=\"col\">Model B</th><th scope=\"col\">Δ (A − B)</th><th scope=\"col\">Favours</th><th scope=\"col\">Direction</th><th scope=\"col\">Notes</th></tr></thead><tbody>");
        foreach (var m in rows)
        {
            var notes = string.Join(" ", new[] { m.A?.Note is { } na ? "A: " + na : null, m.B?.Note is { } nb ? "B: " + nb : null }.OfType<string>());
            sb.Append("<tr><th scope=\"row\" title=\"").Append(H(m.Definition.Explanation)).Append("\">").Append(H(m.Definition.Name)).Append("</th><td>")
              .Append(F(m.A?.Value, m.Definition.Format)).Append("</td><td>").Append(F(m.B?.Value, m.Definition.Format)).Append("</td><td>").Append(D(m.Delta, m.Definition.Format))
              .Append("</td><td>").Append(m.Better switch { "A" => "▲ A", "B" => "▼ B", "Tie" => "● tie", _ => "—" }).Append("</td><td>").Append(H(m.DirectionNote))
              .Append("</td><td>").Append(H(notes)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static void QueryTable(StringBuilder sb, PairComparisonResult r, DetailedReportOptions o, bool content)
    {
        var filters = new[] { ("all", "All"), ("AWins", "Model A wins"), ("BWins", "Model B wins"), ("Tie", "Ties"), ("Disagreement", "Disagreements"),
            ("SafetyConcern", "Safety concerns"), ("Empty", "Empty results"), ("Error", "Errors"), ("Unjudged", "Unjudged") };
        int Count(string key) => key == "all" ? r.Rows.Count : r.Rows.Count(x => x.Outcome.ToString() == key || x.Flags.ToString().Split(", ").Contains(key));
        if (!content && IsSensitive(r))
        {
            // Item and document identifiers and per-item scores are sample-level data for sensitive/unclassified sources.
            sb.Append("<p class=\"notice warn\" role=\"note\">Per-item rows (item and document identifiers, scores) are omitted because the data is sensitive or unclassified. ")
              .Append("Re-export with confirmed sample-level content to include them.</p><table><caption>Item outcomes (counts only)</caption><thead><tr><th scope=\"col\">Category</th><th scope=\"col\">Items</th></tr></thead><tbody>");
            foreach (var (key, label) in filters) sb.Append("<tr><th scope=\"row\">").Append(label).Append("</th><td>").Append(Count(key).ToString(Inv)).Append("</td></tr>");
            sb.Append("</tbody></table>");
            return;
        }
        sb.Append("<div class=\"filters\" role=\"group\" aria-label=\"Filter items\">");
        foreach (var (key, label) in filters)
        {
            int count = Count(key);
            sb.Append("<button type=\"button\" data-filter=\"").Append(key).Append("\" aria-pressed=\"").Append(key == "all" ? "true" : "false").Append("\">")
              .Append(label).Append(" (").Append(count).Append(")</button>");
        }
        sb.Append("</div>");
        var rows = r.Rows.OrderByDescending(x => x.Flags.HasFlag(QueryFlags.SafetyConcern)).ThenByDescending(x => Math.Abs(x.Delta ?? 0)).Take(o.MaxRows).ToList();
        if (r.Rows.Count > rows.Count) sb.Append("<p class=\"muted\">Showing ").Append(rows.Count).Append(" of ").Append(r.Rows.Count).Append(" items (safety concerns and largest differences first).</p>");
        string side = r.IsRetrieval ? "top result (score) · retained/filtered · rel/harm/unjudged" : "prediction (score)";
        sb.Append("<div class=\"scroll\"><table id=\"items\"><caption>Per-item results. Quality per item: ").Append(r.IsRetrieval ? "nDCG@K" : "correct = 1, wrong = 0")
          .Append("; “unknown” = unjudged or failed.</caption><thead><tr><th scope=\"col\">Item</th>");
        if (content) sb.Append("<th scope=\"col\">").Append(r.IsRetrieval ? "Query" : "Text / label").Append("</th>");
        sb.Append("<th scope=\"col\">A: ").Append(side).Append("</th><th scope=\"col\">B: ").Append(side).Append("</th><th scope=\"col\">Quality A / B</th><th scope=\"col\">Δ</th><th scope=\"col\">Outcome</th><th scope=\"col\">Flags / explanation</th></tr></thead><tbody>");
        int thumbs = 0;
        foreach (var x in rows)
        {
            var keys = string.Join(' ', new[] { x.Outcome.ToString() }.Concat(Enum.GetValues<QueryFlags>().Where(f => f != QueryFlags.None && x.Flags.HasFlag(f)).Select(f => f.ToString())));
            sb.Append("<tr data-keys=\"").Append(keys).Append("\"><th scope=\"row\"><code>").Append(H(x.Id)).Append("</code></th>");
            if (content)
            {
                sb.Append("<td>");
                if (x.ImagePath is { } img && thumbs < o.MaxThumbnails && Thumbnail(img) is { } data) { thumbs++; sb.Append("<img alt=\"Query image for ").Append(H(x.Id)).Append("\" src=\"").Append(data).Append("\">"); }
                sb.Append(H(Trim(x.Text))).Append(x.Label is { } l ? " <span class=\"chip\">label: " + H(l) + "</span>" : "").Append("</td>");
            }
            sb.Append("<td>").Append(SideText(x.A, r.IsRetrieval, content)).Append("</td><td>").Append(SideText(x.B, r.IsRetrieval, content)).Append("</td><td>")
              .Append(F(x.A.Quality)).Append(" / ").Append(F(x.B.Quality)).Append("</td><td>").Append(D(x.Delta)).Append("</td><td>").Append(H(x.OutcomeText))
              .Append("</td><td>").Append(H(x.FlagsText)).Append(x.Explanation is { } e ? "<br><span class=\"muted\">" + H(e) + "</span>" : "").Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private static string Trim(string? s) => string.IsNullOrEmpty(s) ? "(no text)" : s.Length > 300 ? s[..300] + "…" : s;

    private static string SideText(SideDetail s, bool retrieval, bool content)
    {
        if (s.Missing) return "missing from run";
        if (s.Error is not null) return "error: " + H(s.Error.Length > 160 ? s.Error[..160] + "…" : s.Error);
        if (!retrieval) return H(s.Predicted ?? "no prediction") + (s.Score is { } sc ? " (" + F(sc, "0.000") + ")" : "");
        if (s.Retained.Count == 0) return $"empty · 0/{s.Filtered}";
        return $"<code>{H(s.TopDocumentId)}</code> ({F(s.TopScore, "0.000")}{(content && s.TopJudgment is { } tj ? ", " + H(tj) : "")}) · {s.Retained.Count}/{s.Filtered} · {s.RelevantRetained}/{s.HarmfulRetained}/{s.UnjudgedRetained}";
    }

    private static string? Thumbnail(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 256 * 1024) return null;
            var mime = info.Extension.ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", _ => null };
            return mime is null ? null : $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Inline SVG charts. Colours come only from CSS classes using --cp-* variables; A is solid, B is dashed/hatched (non-colour cue).</summary>
    private static class Svg
    {
        public static string Bars(IReadOnlyList<PairMetricRow> rows, string a, string b)
        {
            int rowH = 34, w = 640, left = 190, chart = w - left - 70, h = rows.Count * rowH + 40;
            var sb = new StringBuilder();
            sb.Append($"<svg viewBox=\"0 0 {w} {h}\" role=\"img\" aria-labelledby=\"barsT barsD\" class=\"chart\"><title id=\"barsT\">Metric values, Model A vs Model B</title><desc id=\"barsD\">")
              .Append(H(string.Join("; ", rows.Select(r => $"{r.Definition.Name}: A {F(r.A?.Value)}, B {F(r.B?.Value)}")))).Append("</desc>");
            sb.Append($"<defs><pattern id=\"hatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><rect width=\"6\" height=\"6\" class=\"sb\"/><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" class=\"hl\"/></pattern></defs>");
            for (int i = 0; i <= 4; i++) { double x = left + chart * i / 4.0; sb.Append($"<line x1=\"{x:0}\" y1=\"20\" x2=\"{x:0}\" y2=\"{h - 20}\" class=\"grid\"/><text x=\"{x:0}\" y=\"14\" class=\"ax\" text-anchor=\"middle\">{i / 4.0:0.00}</text>"); }
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i]; double y = 24 + i * rowH;
                sb.Append($"<text x=\"{left - 8}\" y=\"{y + 16:0}\" class=\"lb\" text-anchor=\"end\">{H(r.Definition.Name)}</text>");
                double va = Math.Clamp(r.A!.Value ?? 0, 0, 1), vb = Math.Clamp(r.B!.Value ?? 0, 0, 1);
                sb.Append($"<rect x=\"{left}\" y=\"{y:0}\" width=\"{chart * va:0.0}\" height=\"12\" class=\"sa\"><title>A {H(a)}: {F(r.A.Value)}</title></rect>");
                sb.Append($"<rect x=\"{left}\" y=\"{y + 14:0}\" width=\"{chart * vb:0.0}\" height=\"12\" fill=\"url(#hatch)\" class=\"sbo\"><title>B {H(b)}: {F(r.B.Value)}</title></rect>");
                sb.Append($"<text x=\"{left + chart * va + 4:0}\" y=\"{y + 10:0}\" class=\"ax\">A {F(r.A.Value)}</text><text x=\"{left + chart * vb + 4:0}\" y=\"{y + 24:0}\" class=\"ax\">B {F(r.B.Value)}</text>");
            }
            return sb.Append("</svg>").ToString();
        }

        public static string Lines(string name, ThresholdCurve ca, ThresholdCurve cb, string id, string a, string b, double? thresholdA = null, double? thresholdB = null)
        {
            int w = 420, h = 240, l = 44, t = 16, cw = w - l - 16, ch = h - t - 40;
            var sb = new StringBuilder();
            var key = "c" + Math.Abs(id.GetHashCode() % 100000);
            sb.Append($"<svg viewBox=\"0 0 {w} {h}\" role=\"img\" aria-labelledby=\"{key}t {key}d\" class=\"chart\"><title id=\"{key}t\">{H(name)} vs score threshold</title>")
              .Append($"<desc id=\"{key}d\">Model A (solid) and Model B (dashed), each across its own threshold range normalised 0–100%. Exact values are in the data table.</desc>");
            for (int i = 0; i <= 4; i++) { double y = t + ch - ch * i / 4.0; sb.Append($"<line x1=\"{l}\" y1=\"{y:0}\" x2=\"{l + cw}\" y2=\"{y:0}\" class=\"grid\"/><text x=\"{l - 4}\" y=\"{y + 4:0}\" class=\"ax\" text-anchor=\"end\">{i / 4.0:0.00}</text>"); }
            sb.Append($"<text x=\"{l + cw / 2}\" y=\"{h - 8}\" class=\"ax\" text-anchor=\"middle\">threshold position in each model's score range (0% → 100%)</text>");
            foreach (var (c, cls) in new[] { (ca, "la"), (cb, "lb2") })
            {
                var s = c.Series[id]; var pts = new List<string>();
                for (int i = 0; i < s.Length; i++) if (s[i] is { } v) pts.Add($"{l + cw * i / Math.Max(1.0, s.Length - 1):0.0},{t + ch - ch * Math.Clamp(v, 0, 1):0.0}");
                if (pts.Count > 1) sb.Append($"<polyline points=\"{string.Join(' ', pts)}\" class=\"{cls}\"/>");
            }
            int labelRow = 0;
            foreach (var (c, thr, label, cls) in new[] { (ca, thresholdA, "A", "la"), (cb, thresholdB, "B", "lb2") })
            {
                if (thr is not { } v || c.Thresholds.Length < 2) continue;
                double lo = c.Thresholds.Min(), hi = c.Thresholds.Max();
                var frac = hi > lo ? Math.Clamp((v - lo) / (hi - lo), 0, 1) : 0;
                var x = l + cw * frac;
                sb.Append($"<line x1=\"{x:0.0}\" y1=\"{t}\" x2=\"{x:0.0}\" y2=\"{t + ch}\" class=\"{cls}\" stroke-width=\"1\"/>")
                  .Append($"<text x=\"{x + 3:0.0}\" y=\"{t + ch - 6 - 12 * labelRow++:0}\" class=\"ax\">{label} threshold {v.ToString("0.0000", Inv)}</text>");
            }
            sb.Append($"<text x=\"{l + 6}\" y=\"{t + 12}\" class=\"ax\">— A {H(a)}   - - B {H(b)}</text>");
            return sb.Append("</svg>").ToString();
        }

        public static string Radar(IReadOnlyList<PairMetricRow> rows, string a, string b)
        {
            int size = 360; double cx = size / 2.0, cy = size / 2.0 + 6, rad = 120;
            var sb = new StringBuilder();
            sb.Append($"<svg viewBox=\"0 0 {size} {size + 20}\" role=\"img\" aria-labelledby=\"radT radD\" class=\"chart radar\"><title id=\"radT\">Quality radar</title><desc id=\"radD\">")
              .Append(H(string.Join("; ", rows.Select(r => $"{r.Definition.Name}: A {F(r.A?.Value)}, B {F(r.B?.Value)}")))).Append("</desc>");
            int n = rows.Count;
            (double X, double Y) P(int i, double v) { var ang = -Math.PI / 2 + 2 * Math.PI * i / n; return (cx + rad * v * Math.Cos(ang), cy + rad * v * Math.Sin(ang)); }
            foreach (var ring in new[] { 0.25, 0.5, 0.75, 1.0 })
                sb.Append("<polygon class=\"grid\" points=\"").Append(string.Join(' ', Enumerable.Range(0, n).Select(i => { var p = P(i, ring); return $"{p.X:0.0},{p.Y:0.0}"; }))).Append("\"/>");
            for (int i = 0; i < n; i++)
            {
                var p = P(i, 1.12);
                sb.Append($"<text x=\"{p.X:0}\" y=\"{p.Y:0}\" class=\"ax\" text-anchor=\"middle\">{H(rows[i].Definition.Name)}</text>");
            }
            sb.Append("<polygon class=\"ra\" points=\"").Append(string.Join(' ', rows.Select((r, i) => { var p = P(i, Math.Clamp(r.A!.Value ?? 0, 0, 1)); return $"{p.X:0.0},{p.Y:0.0}"; }))).Append("\"/>");
            sb.Append("<polygon class=\"rb\" points=\"").Append(string.Join(' ', rows.Select((r, i) => { var p = P(i, Math.Clamp(r.B!.Value ?? 0, 0, 1)); return $"{p.X:0.0},{p.Y:0.0}"; }))).Append("\"/>");
            sb.Append($"<text x=\"8\" y=\"{size + 14}\" class=\"ax\">solid = A {H(a)}; dashed = B {H(b)}</text>");
            return sb.Append("</svg>").ToString();
        }
    }

    private const string ThemeScript = """
<script>
  (() => {
    const param = new URLSearchParams(window.location.search).get("scoutTheme");
    const theme =
      param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    document.documentElement.setAttribute("data-theme", theme);
  })();
</script>

""";

    private const string FilterScript = """
<script>
  (() => {
    const buttons = document.querySelectorAll("[data-filter]");
    const rows = document.querySelectorAll("#items tbody tr");
    buttons.forEach(b => b.addEventListener("click", () => {
      const key = b.getAttribute("data-filter");
      buttons.forEach(x => x.setAttribute("aria-pressed", x === b ? "true" : "false"));
      rows.forEach(r => { r.hidden = key !== "all" && !(r.getAttribute("data-keys") || "").split(" ").includes(key); });
    }));
    window.addEventListener("beforeprint", () => document.querySelectorAll("details").forEach(d => d.open = true));
  })();
</script>

""";

    private const string Css = """
:root {
  color-scheme: light;
  --cp-bg: #f7f4ef;
  --cp-bg-elevated: #fcfbf8;
  --cp-surface: #ffffff;
  --cp-surface-soft: #f5f5f5;
  --cp-border: #dedede;
  --cp-border-strong: #919191;
  --cp-text: #242424;
  --cp-text-muted: #5c5c5c;
  --cp-text-soft: #6f6f6f;
  --cp-accent: #b11f4b;
  --cp-accent-hover: #9a1a41;
  --cp-accent-soft: rgba(177, 31, 75, 0.08);
  --cp-accent-fg: #ffffff;
  --cp-success: #16a34a;
  --cp-danger: #dc2626;
  --cp-warning: #f59e0b;
  --cp-link: #0078d4;
  --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.12);
  --cp-overlay: rgba(255, 255, 255, 0.8);
  --cp-panel: rgba(255, 255, 255, 0.86);
  --cp-panel-strong: rgba(255, 255, 255, 0.96);
  --cp-sheen: rgba(255, 255, 255, 0.55);
  --cp-highlight: rgba(177, 31, 75, 0.12);
}
html[data-theme="dark"] {
  color-scheme: dark;
  --cp-bg: #3d3b3a;
  --cp-bg-elevated: #343231;
  --cp-surface: #292929;
  --cp-surface-soft: #2e2e2e;
  --cp-border: #474747;
  --cp-border-strong: #5f5f5f;
  --cp-text: #dedede;
  --cp-text-muted: #919191;
  --cp-text-soft: #b0b0b0;
  --cp-accent: #fd8ea1;
  --cp-accent-hover: #fb7b91;
  --cp-accent-soft: rgba(253, 142, 161, 0.14);
  --cp-accent-fg: #1a1a1a;
  --cp-success: #4ade80;
  --cp-danger: #f87171;
  --cp-warning: #fbbf24;
  --cp-link: #4da6ff;
  --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.32);
  --cp-overlay: rgba(41, 41, 41, 0.88);
  --cp-panel: rgba(41, 41, 41, 0.72);
  --cp-panel-strong: rgba(41, 41, 41, 0.96);
  --cp-sheen: rgba(255, 255, 255, 0.04);
  --cp-highlight: rgba(253, 142, 161, 0.12);
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--cp-bg); color: var(--cp-text); font: 14px/1.5 "Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif; }
code { font-family: Consolas, "Courier New", Courier, monospace; font-size: 12px; }
.top, main, footer, .toc { max-width: 1120px; margin: 0 auto; padding: 16px 24px; }
.top h1 { margin: 4px 0; font-size: 26px; }
.vs { color: var(--cp-text-muted); font-weight: 400; }
.eyebrow { margin: 0; color: var(--cp-accent); font-weight: 600; text-transform: uppercase; font-size: 12px; letter-spacing: .04em; }
.muted { color: var(--cp-text-muted); }
.toc ol { columns: 3; margin: 0; padding-left: 20px; }
.toc a { color: var(--cp-link); }
section { background: var(--cp-surface); border: 1px solid var(--cp-border); border-radius: 16px; padding: 16px 20px; margin: 16px 0;
  box-shadow: 0 0 2px rgba(0,0,0,0.12), 0 1px 2px rgba(0,0,0,0.14); break-inside: avoid-page; }
h2 { margin-top: 0; font-size: 18px; }
h3 { font-size: 15px; } h4 { margin: 8px 0 4px; font-size: 13px; }
.headline { font-size: 17px; font-weight: 600; color: var(--cp-accent); }
.notice { border-radius: 10px; padding: 8px 12px; border: 1px solid var(--cp-border-strong); background: var(--cp-surface-soft); }
.notice.warn { border-left: 6px solid var(--cp-warning); }
.notice.bad { border-left: 6px solid var(--cp-danger); }
.notice.info { border-left: 6px solid var(--cp-accent); }
.chip { display: inline-block; padding: 0 8px; border-radius: 10px; background: var(--cp-accent-soft); border: 1px solid var(--cp-border); font-size: 12px; }
table { border-collapse: collapse; width: 100%; margin: 8px 0 12px; font-size: 13px; }
caption { text-align: left; color: var(--cp-text-muted); padding: 4px 0; }
th, td { border-bottom: 1px solid var(--cp-border); padding: 4px 8px; text-align: left; vertical-align: top; }
thead th { background: var(--cp-surface-soft); }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 0; }
dt { color: var(--cp-text-muted); } dd { margin: 0; overflow-wrap: anywhere; }
.grid2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 16px; }
.card { border: 1px solid var(--cp-border); border-radius: 10px; padding: 8px 12px; background: var(--cp-bg-elevated); }
figure { margin: 8px 0; } figcaption { color: var(--cp-text-muted); font-size: 12px; }
.chart { width: 100%; max-width: 680px; height: auto; } .radar { max-width: 380px; }
.chart .grid { stroke: var(--cp-border); fill: none; }
.chart .ax, .chart .lb { fill: var(--cp-text-muted); font-size: 11px; } .chart .lb { fill: var(--cp-text); font-size: 12px; }
.chart .sa { fill: var(--cp-accent); }
.chart .sb { fill: var(--cp-surface-soft); } .chart .hl { stroke: var(--cp-text-muted); stroke-width: 3; }
.chart .sbo { stroke: var(--cp-text-muted); }
.chart .la { fill: none; stroke: var(--cp-accent); stroke-width: 2.5; }
.chart .lb2 { fill: none; stroke: var(--cp-text-muted); stroke-width: 2.5; stroke-dasharray: 6 4; }
.chart .ra { fill: var(--cp-accent-soft); stroke: var(--cp-accent); stroke-width: 2; }
.chart .rb { fill: none; stroke: var(--cp-text-muted); stroke-width: 2; stroke-dasharray: 6 4; }
.heat td.fa, .heat td.fb, .heat td.ft, .heat td.fn { position: relative; font-weight: 600; }
.heat td.fa { background: color-mix(in srgb, var(--cp-success) calc(var(--s) * 45%), transparent); }
.heat td.fb { background: color-mix(in srgb, var(--cp-danger) calc(var(--s) * 45%), transparent); }
.heat td.ft, .heat td.fn { background: var(--cp-surface-soft); }
.filters { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 8px; }
.filters button { font: inherit; font-size: 12px; border: 1px solid var(--cp-border-strong); background: var(--cp-surface); color: var(--cp-text); border-radius: 10px; padding: 2px 10px; cursor: pointer; }
.filters button[aria-pressed="true"] { background: var(--cp-accent); color: var(--cp-accent-fg); border-color: var(--cp-accent); }
.scroll { overflow-x: auto; }
#items img { max-width: 96px; max-height: 72px; display: block; margin-bottom: 4px; border-radius: 4px; }
footer { font-size: 12px; }
@media print {
  body { background: var(--cp-surface); } .toc, .filters { display: none; }
  section { box-shadow: none; border-color: var(--cp-border-strong); }
  #items tr[hidden] { display: table-row; }
}
""";
}
