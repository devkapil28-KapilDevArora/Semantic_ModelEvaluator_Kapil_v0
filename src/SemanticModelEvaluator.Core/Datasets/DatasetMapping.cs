using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Datasets;

[JsonConverter(typeof(JsonStringEnumConverter<TableKind>))]
public enum TableKind { Queries, Documents, Judgments, Examples }

[JsonConverter(typeof(JsonStringEnumConverter<ColumnRole>))]
public enum ColumnRole
{
    Ignore,
    Id,
    QueryId,
    DocumentId,
    Text,
    Text2,
    Prompt,
    ImagePath,
    ExpectedAnswer,
    RelevanceGrade,
    ClassLabel,
    SafetyLabel,
    SimilarityScore,
    Metadata
}

public sealed class TableMapping
{
    public TableKind Kind { get; set; }
    public string Path { get; set; } = "";
    public TableFormat? Format { get; set; }
    /// <summary>Column name → role.</summary>
    public Dictionary<string, ColumnRole> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Must be true before the mapping is used. Suggested mappings start unconfirmed.</summary>
    public bool Confirmed { get; set; }
}

public sealed class MappingProfile
{
    public string Name { get; set; } = "";
    public TableKind Kind { get; set; }
    public Dictionary<string, ColumnRole> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record MappingSuggestion(Dictionary<string, ColumnRole> Columns, IReadOnlyList<string> Ambiguities, IReadOnlyList<string> MissingRequired)
{
    public bool IsComplete => Ambiguities.Count == 0 && MissingRequired.Count == 0;
}

public static class MappingRules
{
    public static IReadOnlyList<ColumnRole> AllowedRoles(TableKind kind) => kind switch
    {
        TableKind.Queries => [ColumnRole.Ignore, ColumnRole.Id, ColumnRole.Text, ColumnRole.ImagePath, ColumnRole.Metadata],
        TableKind.Documents => [ColumnRole.Ignore, ColumnRole.Id, ColumnRole.Text, ColumnRole.ImagePath, ColumnRole.Metadata],
        TableKind.Judgments => [ColumnRole.Ignore, ColumnRole.QueryId, ColumnRole.DocumentId, ColumnRole.RelevanceGrade, ColumnRole.SafetyLabel, ColumnRole.Metadata],
        _ => [ColumnRole.Ignore, ColumnRole.Id, ColumnRole.Text, ColumnRole.Prompt, ColumnRole.Text2, ColumnRole.ClassLabel, ColumnRole.SafetyLabel,
              ColumnRole.SimilarityScore, ColumnRole.ExpectedAnswer, ColumnRole.ImagePath, ColumnRole.Metadata]
    };

    public static IReadOnlyList<ColumnRole> RequiredRoles(TableKind kind) => kind switch
    {
        TableKind.Queries or TableKind.Documents => [ColumnRole.Id],
        TableKind.Judgments => [ColumnRole.QueryId, ColumnRole.DocumentId],
        _ => [ColumnRole.Id]
    };

    // Only exact, conventional names are suggested. Anything else must be mapped by the user.
    private static readonly Dictionary<ColumnRole, string[]> KnownNames = new()
    {
        [ColumnRole.Id] = ["id", "_id"],
        [ColumnRole.QueryId] = ["query_id", "queryid", "qid", "query-id"],
        [ColumnRole.DocumentId] = ["document_id", "documentid", "doc_id", "docid", "corpus_id", "corpus-id"],
        [ColumnRole.Text] = ["text", "query", "content", "sentence", "sentence1", "passage", "title_text"],
        [ColumnRole.Text2] = ["text2", "sentence2", "text_b"],
        [ColumnRole.Prompt] = ["prompt"],
        [ColumnRole.ImagePath] = ["image", "image_path", "imagepath", "image_file"],
        [ColumnRole.ExpectedAnswer] = ["expected", "expected_answer", "answer", "reference"],
        [ColumnRole.RelevanceGrade] = ["grade", "relevance", "score", "label_relevance", "rel"],
        [ColumnRole.ClassLabel] = ["label", "class", "category"],
        [ColumnRole.SafetyLabel] = ["harmful", "harm", "safety", "safety_label", "unsafe"],
        [ColumnRole.SimilarityScore] = ["similarity", "gold_score", "sim_score"],
    };

    public static MappingSuggestion Suggest(TableKind kind, IReadOnlyList<string> columns)
    {
        var allowed = AllowedRoles(kind).ToHashSet();
        var result = new Dictionary<string, ColumnRole>(StringComparer.OrdinalIgnoreCase);
        var candidatesByRole = new Dictionary<ColumnRole, List<string>>();
        foreach (var col in columns)
        {
            var roles = KnownNames.Where(k => allowed.Contains(k.Key) && k.Value.Contains(col.Trim().ToLowerInvariant())).Select(k => k.Key).ToList();
            // In judgment tables "score" means relevance; in example tables it is ambiguous with similarity.
            if (roles.Count == 1) { candidatesByRole.TryAdd(roles[0], []); candidatesByRole[roles[0]].Add(col); }
            result[col] = ColumnRole.Ignore;
        }
        var ambiguities = new List<string>();
        foreach (var (role, cols) in candidatesByRole)
        {
            if (cols.Count == 1) result[cols[0]] = role;
            else ambiguities.Add($"{role} could be any of: {string.Join(", ", cols)}");
        }
        var missing = RequiredRoles(kind).Where(r => !result.ContainsValue(r)).Select(r => r.ToString()).ToList();
        if (kind is TableKind.Queries or TableKind.Documents or TableKind.Examples &&
            !result.ContainsValue(ColumnRole.Text) && !result.ContainsValue(ColumnRole.ImagePath) && !result.ContainsValue(ColumnRole.Prompt))
            missing.Add("Text, Prompt or ImagePath");
        return new MappingSuggestion(result, ambiguities, missing);
    }

    public static ValidationReport Validate(TableKind kind, IReadOnlyDictionary<string, ColumnRole> mapping)
    {
        var report = new ValidationReport();
        var allowed = AllowedRoles(kind).ToHashSet();
        foreach (var (col, role) in mapping)
            if (!allowed.Contains(role)) report.Error($"Column '{col}' cannot be mapped to {role} in a {kind} table.");
        foreach (var g in mapping.Where(m => m.Value is not (ColumnRole.Ignore or ColumnRole.Metadata)).GroupBy(m => m.Value))
            if (g.Count() > 1) report.Error($"{g.Key} is mapped from more than one column ({string.Join(", ", g.Select(x => x.Key))}).");
        foreach (var r in RequiredRoles(kind))
            if (!mapping.Values.Contains(r)) report.Error($"A {kind} table requires a column mapped to {r}.");
        if (kind is TableKind.Queries or TableKind.Documents or TableKind.Examples &&
            !mapping.Values.Any(v => v is ColumnRole.Text or ColumnRole.ImagePath or ColumnRole.Prompt))
            report.Error($"A {kind} table requires a Text, Prompt or ImagePath column.");
        return report;
    }
}

public sealed class DatasetManifest
{
    public string Format { get; set; } = "smds";
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1";
    public EvalTask Task { get; set; } = EvalTask.Retrieval;
    public string Description { get; set; } = "";
    public string DataClassification { get; set; } = "Unclassified";
    public string? PositiveLabel { get; set; }
    public bool Synthetic { get; set; }
    public List<TableMapping> Tables { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DatasetManifest Load(string path)
    {
        var m = JsonSerializer.Deserialize<DatasetManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Empty dataset manifest.");
        if (m.Format != "smds") throw new InvalidDataException("Not a Semantic_ModelEvaluator dataset manifest (format must be 'smds').");
        if (m.FormatVersion > 1) throw new InvalidDataException($"Dataset manifest version {m.FormatVersion} is newer than this application supports.");
        foreach (var t in m.Tables) t.Columns = new Dictionary<string, ColumnRole>(t.Columns, StringComparer.OrdinalIgnoreCase);
        return m;
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

public static class DatasetLoader
{
    public static EvalDataset LoadManifest(string manifestPath)
    {
        var manifest = DatasetManifest.Load(manifestPath);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var tables = new List<(TableMapping, TabularData)>();
        foreach (var t in manifest.Tables)
        {
            var path = ResolveRelative(baseDir, t.Path);
            tables.Add((t, TabularReader.Read(path, t.Format)));
        }
        var hash = ComputeHash([manifestPath, .. manifest.Tables.Select(t => ResolveRelative(baseDir, t.Path))]);
        return DatasetBuilder.Build(manifest, tables, baseDir, Path.GetFullPath(manifestPath), hash);
    }

    public static string ResolveRelative(string baseDir, string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
        return full;
    }

    public static string ComputeHash(IEnumerable<string> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in files)
        {
            using var s = File.OpenRead(f);
            var buffer = new byte[81920];
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

public static class DatasetBuilder
{
    public static EvalDataset Build(DatasetManifest manifest, IReadOnlyList<(TableMapping Mapping, TabularData Data)> tables,
        string? baseDir, string? sourcePath, string contentHash)
    {
        var issues = new List<ValidationIssue>();
        var queries = new List<QueryItem>();
        var docs = new List<DocumentItem>();
        var judgments = new List<Judgment>();
        var examples = new List<LabeledExample>();

        foreach (var (map, data) in tables)
        {
            var mv = MappingRules.Validate(map.Kind, map.Columns);
            if (!mv.IsValid) throw new InvalidDataException($"Invalid mapping for {map.Kind} table '{Path.GetFileName(map.Path)}':{Environment.NewLine}{mv}");
            if (!map.Confirmed) throw new InvalidDataException($"The column mapping for '{Path.GetFileName(map.Path)}' has not been confirmed.");
            foreach (var col in map.Columns.Keys)
                if (data.IndexOf(col) < 0) throw new InvalidDataException($"Mapped column '{col}' is not present in '{Path.GetFileName(map.Path)}'.");

            string? Col(ColumnRole r) => map.Columns.FirstOrDefault(c => c.Value == r).Key;
            int Idx(ColumnRole r) => Col(r) is { } c ? data.IndexOf(c) : -1;
            var metaCols = map.Columns.Where(c => c.Value == ColumnRole.Metadata).Select(c => (c.Key, data.IndexOf(c.Key))).ToList();
            string? Get(string?[] row, int i) => i >= 0 && i < row.Length ? row[i] : null;
            Dictionary<string, string>? Meta(string?[] row)
            {
                if (metaCols.Count == 0) return null;
                var d = new Dictionary<string, string>();
                foreach (var (name, i) in metaCols) if (Get(row, i) is { } v) d[name] = v;
                return d;
            }
            string? Img(string?[] row) => Get(row, Idx(ColumnRole.ImagePath)) is { Length: > 0 } p && baseDir is not null ? DatasetLoader.ResolveRelative(baseDir, p) : null;

            int rowNo = 1;
            foreach (var row in data.Rows)
            {
                rowNo++;
                switch (map.Kind)
                {
                    case TableKind.Queries:
                    case TableKind.Documents:
                    {
                        var id = Get(row, Idx(ColumnRole.Id));
                        if (string.IsNullOrWhiteSpace(id)) { issues.Add(new(ValidationSeverity.Warning, $"{map.Kind} row {rowNo}: missing id; row skipped.")); continue; }
                        var text = Get(row, Idx(ColumnRole.Text)) ?? "";
                        var img = Img(row);
                        if (text.Length == 0 && img is null) issues.Add(new(ValidationSeverity.Warning, $"{map.Kind} '{id}' has no text or image."));
                        if (map.Kind == TableKind.Queries) queries.Add(new(id, text, img, Meta(row)));
                        else docs.Add(new(id, text, img, Meta(row)));
                        break;
                    }
                    case TableKind.Judgments:
                    {
                        var q = Get(row, Idx(ColumnRole.QueryId));
                        var d = Get(row, Idx(ColumnRole.DocumentId));
                        if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(d)) { issues.Add(new(ValidationSeverity.Warning, $"Judgment row {rowNo}: missing query or document id; skipped.")); continue; }
                        double? grade = null;
                        if (Get(row, Idx(ColumnRole.RelevanceGrade)) is { Length: > 0 } g)
                        {
                            if (double.TryParse(g, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gv)) grade = gv;
                            else issues.Add(new(ValidationSeverity.Warning, $"Judgment row {rowNo}: grade '{g}' is not numeric; treated as unjudged."));
                        }
                        bool? harmful = ParseSafety(Get(row, Idx(ColumnRole.SafetyLabel)), rowNo, issues);
                        judgments.Add(new(q, d, grade, harmful));
                        break;
                    }
                    default:
                    {
                        var id = Get(row, Idx(ColumnRole.Id));
                        if (string.IsNullOrWhiteSpace(id)) { issues.Add(new(ValidationSeverity.Warning, $"Example row {rowNo}: missing id; skipped.")); continue; }
                        double? gold = null;
                        if (Get(row, Idx(ColumnRole.SimilarityScore)) is { Length: > 0 } s &&
                            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sv)) gold = sv;
                        var label = Get(row, Idx(ColumnRole.ClassLabel));
                        var safety = Get(row, Idx(ColumnRole.SafetyLabel));
                        if (label is null && safety is not null)
                        {
                            var h = ParseSafety(safety, rowNo, issues);
                            label = h is null ? null : h.Value ? "harmful" : "safe";
                        }
                        examples.Add(new(id, Get(row, Idx(ColumnRole.Text)) ?? Get(row, Idx(ColumnRole.Prompt)) ?? "", Get(row, Idx(ColumnRole.Text2)),
                            string.IsNullOrWhiteSpace(label) ? null : label.Trim(), gold, Get(row, Idx(ColumnRole.ExpectedAnswer)), Img(row), Meta(row)));
                        break;
                    }
                }
            }
        }

        var ds = new EvalDataset
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            Task = manifest.Task,
            Description = manifest.Description,
            DataClassification = manifest.DataClassification,
            PositiveLabel = manifest.PositiveLabel,
            IsSynthetic = manifest.Synthetic,
            SourcePath = sourcePath,
            ContentHash = contentHash,
            Queries = queries,
            Documents = docs,
            Judgments = judgments,
            Examples = examples,
            ValidationIssues = issues
        };
        DatasetValidator.Validate(ds, issues);
        return ds;
    }

    private static bool? ParseSafety(string? value, int rowNo, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        switch (value.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "harmful": case "unsafe": return true;
            case "0": case "false": case "no": case "safe": case "benign": return false;
            default:
                issues.Add(new(ValidationSeverity.Warning, $"Row {rowNo}: safety label '{value}' not recognised; treated as unjudged."));
                return null;
        }
    }
}

public static class DatasetValidator
{
    public static void Validate(EvalDataset ds, List<ValidationIssue> issues)
    {
        void Dupes<T>(IEnumerable<T> items, Func<T, string> id, string what)
        {
            var d = items.GroupBy(id).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
            if (d.Count > 0) issues.Add(new(ValidationSeverity.Error, $"Duplicate {what} ids: {string.Join(", ", d)}"));
        }
        Dupes(ds.Queries, q => q.Id, "query");
        Dupes(ds.Documents, q => q.Id, "document");
        Dupes(ds.Examples, q => q.Id, "example");
        if (ds.Judgments.GroupBy(j => (j.QueryId, j.DocumentId)).Any(g => g.Count() > 1))
            issues.Add(new(ValidationSeverity.Error, "Duplicate query/document judgments must be resolved explicitly."));

        switch (ds.Task)
        {
            case EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking:
                if (ds.Queries.Count == 0) issues.Add(new(ValidationSeverity.Error, "Retrieval datasets need a queries table."));
                if (ds.Documents.Count == 0) issues.Add(new(ValidationSeverity.Error, "Retrieval datasets need a documents table."));
                if (!ds.HasRelevanceJudgments) issues.Add(new(ValidationSeverity.Warning, "No relevance judgments: quality metrics cannot be computed; only score distributions and coverage."));
                var qids = ds.Queries.Select(q => q.Id).ToHashSet();
                var dids = ds.Documents.Select(q => q.Id).ToHashSet();
                int orphanQ = ds.Judgments.Count(j => !qids.Contains(j.QueryId));
                int orphanD = ds.Judgments.Count(j => !dids.Contains(j.DocumentId));
                if (orphanQ > 0) issues.Add(new(ValidationSeverity.Warning, $"{orphanQ} judgments reference unknown queries."));
                if (orphanD > 0) issues.Add(new(ValidationSeverity.Warning, $"{orphanD} judgments reference unknown documents."));
                if (ds.Queries.Count > 0 && ds.JudgmentCoverage < 1)
                    issues.Add(new(ValidationSeverity.Info, $"Judgment coverage: {ds.JudgmentCoverage:P0} of queries have judgments; unjudged queries are excluded from quality metrics."));
                break;
            case EvalTask.Classification or EvalTask.Safety:
                if (ds.Examples.Count == 0) issues.Add(new(ValidationSeverity.Error, "Classification datasets need an examples table."));
                var labels = ds.Examples.Select(e => e.Label).Where(l => l is not null).Distinct().ToList();
                if (ds.PositiveLabel is not null && (labels.Count > 2 || !labels.Contains(ds.PositiveLabel)))
                    issues.Add(new(ValidationSeverity.Error, "A positive label requires a binary dataset and must match a ground-truth label."));
                if (!ds.HasLabels) issues.Add(new(ValidationSeverity.Warning, "No labels: classification metrics cannot be computed."));
                else if (ds.LabelCoverage < 1) issues.Add(new(ValidationSeverity.Info, $"Label coverage {ds.LabelCoverage:P0}; unlabelled examples are excluded (never treated as safe/negative)."));
                break;
            case EvalTask.Generative:
                if (ds.Examples.Count == 0) issues.Add(new(ValidationSeverity.Error, "Generative evaluation needs an examples table."));
                issues.Add(new(ValidationSeverity.Info, "Generative outputs can be inspected; quality requires a validated metric plugin. No automatic correctness score is inferred."));
                break;
            case EvalTask.SemanticSimilarity:
                if (ds.Examples.Count == 0) issues.Add(new(ValidationSeverity.Error, "Similarity datasets need an examples table with text pairs."));
                if (ds.Examples.Any(e => e.Text2 is null)) issues.Add(new(ValidationSeverity.Error, "Every similarity example needs Text2."));
                if (!ds.HasGoldScores) issues.Add(new(ValidationSeverity.Warning, "No gold similarity scores: correlation metrics cannot be computed."));
                break;
        }
        if (ds.HasImages)
            foreach (var p in ds.Documents.Select(d => d.ImagePath).Concat(ds.Examples.Select(e => e.ImagePath)).Where(p => p is not null).Take(1000))
                if (!File.Exists(p)) { issues.Add(new(ValidationSeverity.Warning, $"Image not found: {Path.GetFileName(p)} (and possibly others).")); break; }
    }
}
