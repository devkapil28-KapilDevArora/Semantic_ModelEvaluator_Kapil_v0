using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemanticModelEvaluator.Core;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Providers.Datasets;

public sealed record DatasetImportInput(string Path, TableMapping Mapping, HubDatasetInfo? Hub = null, string? RemoteFile = null,
    string? Configuration = null, string? Split = null, bool CaptionPairs = false, bool ImageQueries = false);

public static class DatasetImport
{
    public static async Task<string> BuildAsync(string name, EvalTask task, IReadOnlyList<DatasetImportInput> inputs,
        int rowsPerFile, string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || inputs.Count == 0) throw new ArgumentException("Name the dataset and add at least one explicitly mapped input.");
        if (inputs.Any(i => !i.Mapping.Confirmed)) throw new InvalidOperationException("Confirm every input mapping before importing.");
        if (inputs.Any(i => i.Hub is not null) && inputs.Any(i => i.Hub is null))
            throw new InvalidOperationException("Public Hub and local/internal files cannot be mixed into a publicly classified dataset.");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("Normalize into a new, empty directory so prior datasets cannot be overwritten.");
        Directory.CreateDirectory(directory);
        var manifest = new DatasetManifest { Name = name, Task = task, DataClassification = inputs.All(i => i.Hub is not null) ? "Public (license restrictions apply)" : "Unclassified" };
        var allTables = new List<(TableMapping, TabularData)>();
        var components = new List<string>();
        for (int i = 0; i < inputs.Count; i++)
        {
            var entry = inputs[i];
            var fields = entry.Mapping.Columns.Where(c => c.Value != ColumnRole.Ignore).Select(c => c.Key).ToArray();
            var table = await DatasetTableReader.ReadAsync(entry.Path, fields, rowsPerFile, Path.Combine(directory, "images"), ct).ConfigureAwait(false);
            var images = entry.Mapping.Columns.Where(c => c.Value == ColumnRole.ImagePath).Select(c => table.IndexOf(c.Key)).ToArray();
            foreach (var row in table.Rows)
                foreach (var index in images)
                    if (index >= 0 && row[index] is { Length: > 0 } image)
                    {
                        var imagePath = Path.GetFullPath(image, Path.GetDirectoryName(Path.GetFullPath(entry.Path))!);
                        var assetRoot = Path.GetFullPath(Path.Combine(directory, "images")) + Path.DirectorySeparatorChar;
                        if (entry.Hub is not null && !imagePath.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Public table image paths must resolve to embedded images in this import. Arbitrary local paths and remote URLs are blocked.");
                        if (!File.Exists(imagePath)) throw new FileNotFoundException("A mapped image is not available locally. Import the authorized image payload first.");
                        if (new FileInfo(imagePath).Length > 20 * 1024 * 1024) throw new InvalidDataException("A mapped image exceeds the 20 MB limit.");
                        row[index] = DatasetTableReader.SaveImage(await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false), Path.Combine(directory, "images"));
                    }
            if (entry.CaptionPairs)
            {
                if (inputs.Count != 1 || task is not (EvalTask.Retrieval or EvalTask.ImageTextRetrieval))
                    throw new InvalidOperationException("Caption-pair normalization requires one input and a retrieval task.");
                var captionTables = CaptionTables(table, entry.Mapping, entry.ImageQueries);
                allTables.AddRange(captionTables);
            }
            else
            {
                allTables.Add((entry.Mapping, table));
            }
            await using var stream = File.OpenRead(entry.Path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            components.Add(entry.Hub is { } hub ? $"{hub.Repository}@{hub.Revision}/{entry.RemoteFile}; config={entry.Configuration}; split={entry.Split}; sha256={hash}; license={hub.License}"
                : $"local file sha256={hash}");
        }
        for (int i = 0; i < allTables.Count; i++)
        {
            var (mapping, data) = allTables[i];
            foreach (var column in mapping.Columns.Where(c => c.Value == ColumnRole.ImagePath))
            {
                var index = data.IndexOf(column.Key);
                foreach (var row in data.Rows)
                    if (row[index] is { Length: > 0 } image) row[index] = Path.GetRelativePath(directory, image);
            }
            string path = Path.Combine(directory, $"table-{i}.jsonl");
            var lines = data.Rows.Select(row => JsonSerializer.Serialize(data.Columns.Select((c, j) => (c, Value: row[j]))
                .ToDictionary(x => x.c, x => x.Value)));
            await File.WriteAllLinesAsync(path, lines, ct).ConfigureAwait(false);
            manifest.Tables.Add(new TableMapping { Path = Path.GetFileName(path), Kind = mapping.Kind, Format = TableFormat.JsonLines,
                Columns = new(mapping.Columns), Confirmed = true });
        }
        var mappingIdentity = JsonSerializer.Serialize(new { components, rowsPerFile, task, mappings = manifest.Tables, imageQueries = inputs.Select(i => i.ImageQueries) });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mappingIdentity))).ToLowerInvariant();
        manifest.Id = "dataset-" + fingerprint[..20];
        manifest.Version = fingerprint;
        manifest.Description = $"Explicit static-file subset: first {rowsPerFile} rows per input, without repository code execution. " +
            "Judgments not present remain unknown. Truncated corpora are not full-benchmark results.";
        manifest.Provenance = new DatasetProvenance
        {
            Source = inputs.All(i => i.Hub is not null) ? DatasetSource.HuggingFace : DatasetSource.Local,
            Repository = inputs[0].Hub?.Repository, Revision = inputs[0].Hub?.Revision,
            Configuration = inputs[0].Configuration, Split = inputs[0].Split,
            License = inputs[0].Hub?.License, Components = components, Subset = manifest.Description
        };
        var assetDirectory = Path.Combine(directory, "images");
        if (Directory.Exists(assetDirectory))
            foreach (var image in Directory.EnumerateFiles(assetDirectory))
                manifest.Assets[Path.GetRelativePath(directory, image)] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(image, ct).ConfigureAwait(false))).ToLowerInvariant();
        var manifestPath = Path.Combine(directory, "dataset.smds.json");
        bool validated = false;
        try
        {
            manifest.Save(manifestPath);
            var dataset = await Task.Run(() => DatasetLoader.LoadManifest(manifestPath), ct).ConfigureAwait(false);
            var errors = dataset.ValidationIssues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors.Select(e => e.Message)));
            ct.ThrowIfCancellationRequested();
            validated = true;
        }
        finally { if (!validated && File.Exists(manifestPath)) File.Delete(manifestPath); }
        return manifestPath;
    }

    private static List<(TableMapping, TabularData)> CaptionTables(TabularData table, TableMapping map, bool imageQueries)
    {
        var textColumn = map.Columns.SingleOrDefault(x => x.Value == ColumnRole.Text).Key;
        var imageColumn = map.Columns.SingleOrDefault(x => x.Value == ColumnRole.ImagePath).Key;
        int textIndex = textColumn is null ? -1 : table.IndexOf(textColumn), imageIndex = imageColumn is null ? -1 : table.IndexOf(imageColumn);
        if (textIndex < 0 || imageIndex < 0) throw new InvalidDataException("Caption pairs require one Text and one embedded ImagePath column.");
        var captions = new List<string?[]>();
        var images = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        var judgments = new List<string?[]>();
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var image = row[imageIndex];
            if (string.IsNullOrWhiteSpace(row[textIndex]) || image is null || !File.Exists(image))
                throw new InvalidDataException("Caption pairs require a nonempty caption and a local embedded image in every row.");
            var imageId = "image-" + Path.GetFileNameWithoutExtension(image);
            var captionId = "caption-" + i;
            captions.Add([captionId, row[textIndex]]);
            images.TryAdd(imageId, [imageId, image]);
            judgments.Add(imageQueries ? [imageId, captionId, "1"] : [captionId, imageId, "1"]);
        }
        return [
            Make(imageQueries ? TableKind.Documents : TableKind.Queries, ["id", "text"], captions, [ColumnRole.Id, ColumnRole.Text]),
            Make(imageQueries ? TableKind.Queries : TableKind.Documents, ["id", "image"], images.Values.ToList(), [ColumnRole.Id, ColumnRole.ImagePath]),
            Make(TableKind.Judgments, ["query_id", "document_id", "grade"], judgments, [ColumnRole.QueryId, ColumnRole.DocumentId, ColumnRole.RelevanceGrade])
        ];
    }

    private static (TableMapping, TabularData) Make(TableKind kind, string[] columns, List<string?[]> rows, ColumnRole[] roles) =>
        (new TableMapping { Kind = kind, Columns = columns.Select((c, i) => (c, roles[i])).ToDictionary(x => x.c, x => x.Item2), Confirmed = true },
            new TabularData { SourcePath = "", Format = TableFormat.JsonLines, Columns = columns, Rows = rows });
}
