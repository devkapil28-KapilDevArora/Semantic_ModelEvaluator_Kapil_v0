using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Providers.Datasets;

public sealed record PublicDatasetRecipe(string Name, string Description, EvalTask Task, int RowsPerFile,
    IReadOnlyList<PublicDatasetInput> Inputs)
{
    public override string ToString() => Name;
}

public sealed record PublicDatasetInput(string Repository, string File, string Configuration, string Split,
    TableKind Kind, IReadOnlyDictionary<string, ColumnRole> Columns, bool CaptionPairs = false);

public sealed record PublicDatasetPlan(PublicDatasetRecipe Recipe,
    IReadOnlyList<(PublicDatasetInput Input, HubDatasetInfo Info, HubDatasetFile File)> Files)
{
    public long Bytes => Files.Sum(f => f.File.Bytes!.Value);
    public string Summary => $"{Recipe.Name}\n{Recipe.Description}\n" +
        $"Download: {Bytes / 1048576d:0.00} MB; first {Recipe.RowsPerFile} rows per file.\n" +
        string.Join("\n", Files.Select(f =>
            $"https://huggingface.co/datasets/{f.Info.Repository}/tree/{f.Info.Revision}\n" +
            $"  {f.File.Path} | {f.Input.Configuration}/{f.Input.Split} | license: {f.Info.License}\n" +
            $"  Mapping: {string.Join(", ", f.Input.Columns.Select(c => c.Key + " -> " + c.Value))}"));
}

/// <summary>Explicit, reviewable static-file recipes; never execute repository scripts or infer safety labels.</summary>
public static class PublicDatasetStarters
{
    public static IReadOnlyList<PublicDatasetRecipe> All { get; } =
    [
        new("COCO image-caption starter", "Public FlagEval COCO sample: 100 image/prompt positive pairs. " +
            "This is a retrieval smoke test, not RAI threshold evidence or an official COCO benchmark. " +
            "The Hub card declares Apache-2.0; review original image rights as well. Other image/query pairs remain unjudged.",
            EvalTask.ImageTextRetrieval, 100,
            [new("FlagEval/coco_val2014_sampled", "data/test-00000-of-00001.parquet", "default", "test",
                TableKind.Documents, new Dictionary<string, ColumnRole>
                { ["image/bytes"] = ColumnRole.ImagePath, ["prompt"] = ColumnRole.Text }, true)]),
        new("SciFact text retrieval starter", "Scientific claims and abstracts with test relevance judgments. " +
            "Uses abstract text (not concatenated titles); queries without test judgments remain unjudged. " +
            "Not a safety dataset or an official benchmark score.",
            EvalTask.Retrieval, 10000,
            [
                new("BeIR/scifact", "queries/queries-00000-of-00001.parquet", "queries", "queries", TableKind.Queries,
                    new Dictionary<string, ColumnRole> { ["_id"] = ColumnRole.Id, ["text"] = ColumnRole.Text }),
                new("BeIR/scifact", "corpus/corpus-00000-of-00001.parquet", "corpus", "corpus", TableKind.Documents,
                    new Dictionary<string, ColumnRole> { ["_id"] = ColumnRole.Id, ["text"] = ColumnRole.Text }),
                new("BeIR/scifact-qrels", "test.tsv", "default", "test", TableKind.Judgments,
                    new Dictionary<string, ColumnRole> { ["query-id"] = ColumnRole.QueryId,
                        ["corpus-id"] = ColumnRole.DocumentId, ["score"] = ColumnRole.RelevanceGrade })
            ])
    ];

    public static async Task<PublicDatasetPlan> InspectAsync(HuggingFaceDatasets hub, PublicDatasetRecipe recipe,
        long byteBudget, CancellationToken ct)
    {
        var metadata = new Dictionary<string, HubDatasetInfo>(StringComparer.Ordinal);
        var files = new List<(PublicDatasetInput, HubDatasetInfo, HubDatasetFile)>();
        long total = 0;
        foreach (var input in recipe.Inputs)
        {
            if (!metadata.TryGetValue(input.Repository, out var info))
                metadata[input.Repository] = info = await hub.InspectAsync(input.Repository, "main", ct).ConfigureAwait(false);
            if (!info.CanDownload) throw new InvalidOperationException($"{input.Repository}: {info.Access}");
            var file = info.Files.SingleOrDefault(f => f.Path == input.File)
                ?? throw new InvalidDataException($"The starter file {input.File} is no longer published. Use advanced import to review the changed schema.");
            if (file.Bytes is not > 0) throw new InvalidDataException("A starter file has unknown size; no download was started.");
            total = checked(total + file.Bytes.Value);
            if (total > byteBudget) throw new InvalidOperationException("The starter exceeds the total download budget. Increase it explicitly or choose another dataset.");
            files.Add((input, info, file));
        }
        return new(recipe, files);
    }

    public static async Task<string> ImportAsync(HuggingFaceDatasets hub, PublicDatasetPlan plan, long byteBudget,
        bool licenseAndMappingAccepted, string directory, IProgress<string>? progress, CancellationToken ct)
    {
        if (!licenseAndMappingAccepted) throw new InvalidOperationException("Review and accept the source licenses and starter mappings first.");
        if (plan.Bytes > byteBudget) throw new InvalidOperationException("The starter exceeds the total download budget.");
        var inputs = new List<DatasetImportInput>();
        foreach (var (input, info, file) in plan.Files)
        {
            var path = await hub.DownloadAsync(info, file, byteBudget, true, progress, ct).ConfigureAwait(false);
            var columns = await DatasetTableReader.ColumnsAsync(path, ct).ConfigureAwait(false);
            if (input.Columns.Keys.Any(c => !columns.Contains(c, StringComparer.Ordinal)))
                throw new InvalidDataException($"{input.Repository}/{file.Path}: schema changed. Review columns in advanced import; no mapping was guessed.");
            inputs.Add(new(path, new TableMapping { Path = path, Kind = input.Kind, Confirmed = true,
                Columns = new(input.Columns) }, info, file.Path, input.Configuration, input.Split, input.CaptionPairs));
        }
        progress?.Report("Building and validating the local dataset...");
        return await DatasetImport.BuildAsync(plan.Recipe.Name, plan.Recipe.Task, inputs,
            plan.Recipe.RowsPerFile, directory, ct).ConfigureAwait(false);
    }
}
