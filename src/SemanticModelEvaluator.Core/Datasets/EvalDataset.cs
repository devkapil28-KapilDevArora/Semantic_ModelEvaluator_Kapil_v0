using System.Text.Json.Serialization;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Datasets;

public sealed record QueryItem(string Id, string Text, string? ImagePath = null, Dictionary<string, string>? Metadata = null);

public sealed record DocumentItem(string Id, string Text, string? ImagePath = null, Dictionary<string, string>? Metadata = null);

/// <summary>Relevance judgment. <see cref="Grade"/> &gt; 0 means relevant. <see cref="Harmful"/> null means "not judged for safety".</summary>
public sealed record Judgment(string QueryId, string DocumentId, double? Grade, bool? Harmful = null);

/// <summary>Single-table example for classification, similarity, safety and generative tasks.</summary>
public sealed record LabeledExample(
    string Id,
    string Text,
    string? Text2 = null,
    string? Label = null,
    double? GoldScore = null,
    string? ExpectedAnswer = null,
    string? ImagePath = null,
    Dictionary<string, string>? Metadata = null);

public sealed class EvalDataset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1";
    public EvalTask Task { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Data classification label (e.g. Public, General, Confidential). Exports of example text warn when not Public.</summary>
    public string DataClassification { get; init; } = "Unclassified";
    public string? SourcePath { get; init; }
    public string ContentHash { get; init; } = "";
    public string? PositiveLabel { get; init; }
    public bool IsSynthetic { get; init; }
    public List<QueryItem> Queries { get; init; } = [];
    public List<DocumentItem> Documents { get; init; } = [];
    public List<Judgment> Judgments { get; init; } = [];
    public List<LabeledExample> Examples { get; init; } = [];
    public List<ValidationIssue> ValidationIssues { get; init; } = [];

    [JsonIgnore] public bool HasSafetyJudgments => Judgments.Any(j => j.Harmful.HasValue);
    [JsonIgnore] public bool HasRelevanceJudgments => Judgments.Any(j => j.Grade.HasValue);
    [JsonIgnore] public bool HasLabels => Examples.Any(e => e.Label is not null);
    [JsonIgnore] public bool HasGoldScores => Examples.Any(e => e.GoldScore.HasValue);
    [JsonIgnore] public bool HasImages => Documents.Any(d => d.ImagePath is not null) || Queries.Any(q => q.ImagePath is not null) || Examples.Any(e => e.ImagePath is not null);

    [JsonIgnore]
    public int RecordCount => Task is EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking ? Queries.Count : Examples.Count;

    private JudgmentIndex? index;
    [JsonIgnore] public JudgmentIndex JudgmentIndex => index ??= new JudgmentIndex(Judgments);

    private Dictionary<string, QueryItem>? queryMap;
    private Dictionary<string, DocumentItem>? docMap;
    private Dictionary<string, LabeledExample>? exampleMap;
    public QueryItem? FindQuery(string id) => (queryMap ??= Queries.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First())).GetValueOrDefault(id);
    public DocumentItem? FindDocument(string id) => (docMap ??= Documents.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First())).GetValueOrDefault(id);
    public LabeledExample? FindExample(string id) => (exampleMap ??= Examples.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First())).GetValueOrDefault(id);

    public DatasetReference ToReference() => new(Id, Name, Version, Task, SourcePath, ContentHash);

    /// <summary>Fraction of queries that have at least one relevance judgment.</summary>
    public double JudgmentCoverage => Queries.Count == 0 ? 0 : (double)Queries.Count(q => JudgmentIndex.HasJudgments(q.Id)) / Queries.Count;

    public double LabelCoverage => Examples.Count == 0 ? 0 : (double)Examples.Count(e => e.Label is not null || e.GoldScore is not null) / Examples.Count;
}

public sealed record DatasetReference(string Id, string Name, string Version, EvalTask Task, string? SourcePath, string ContentHash);

public sealed class JudgmentIndex
{
    private readonly Dictionary<string, Dictionary<string, Judgment>> byQuery = new(StringComparer.Ordinal);

    public JudgmentIndex(IEnumerable<Judgment> judgments)
    {
        foreach (var j in judgments)
        {
            if (!byQuery.TryGetValue(j.QueryId, out var docs)) byQuery[j.QueryId] = docs = new(StringComparer.Ordinal);
            docs[j.DocumentId] = j;
        }
    }

    public bool HasJudgments(string queryId) => byQuery.ContainsKey(queryId);

    public Judgment? Get(string queryId, string documentId) =>
        byQuery.TryGetValue(queryId, out var docs) && docs.TryGetValue(documentId, out var j) ? j : null;

    public IEnumerable<Judgment> ForQuery(string queryId) =>
        byQuery.TryGetValue(queryId, out var docs) ? docs.Values : [];

    public int RelevantCount(string queryId) => ForQuery(queryId).Count(j => j.Grade > 0);

    /// <summary>Ideal grades sorted descending, used for NDCG normalisation.</summary>
    public double[] IdealGrades(string queryId) =>
        [.. ForQuery(queryId).Where(j => j.Grade > 0).Select(j => j.Grade!.Value).OrderByDescending(g => g)];
}
