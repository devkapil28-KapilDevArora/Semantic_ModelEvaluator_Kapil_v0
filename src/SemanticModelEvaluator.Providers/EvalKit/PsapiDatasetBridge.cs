using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.EvalKit;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Providers.EvalKit;

/// <summary>A local execution snapshot; app metrics still use the original judgments, including unknown safety labels.</summary>
public sealed record PsapiDatasetBridge(string ManifestPath, EvalKitDatasetIdentity ExecutionIdentity, DatasetReference Original)
{
    public DatasetReference MatchResult(EvalKitDatasetIdentity identity)
    {
        var mismatches = ExecutionIdentity.Mismatches(identity);
        if (mismatches.Count > 0)
            throw new InvalidDataException("PSAPI returned a different execution dataset: " + string.Join("; ", mismatches));
        return Original;
    }

    public static async Task<PsapiDatasetBridge> CreateAsync(EvalDataset source, string directory, CancellationToken ct)
    {
        DatasetPolicy.Ensure(source, DatasetOperation.Evaluate);
        if (source.Task is not (EvalTask.Retrieval or EvalTask.ImageTextRetrieval) ||
            source.Queries.Count == 0 || source.Documents.Count == 0 ||
            source.Queries.Any(q => q.ImagePath is not null || string.IsNullOrWhiteSpace(q.Text)))
            throw new InvalidOperationException("PSAPI requires text queries and a non-empty text or image corpus.");
        if (source.SourcePath is not { } sourcePath || !sourcePath.EndsWith(".smds.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a prepared app dataset needs the local PSAPI adapter.");
        var verified = DatasetLoader.LoadManifest(sourcePath);
        if (verified.ContentHash != source.ContentHash || verified.ValidationIssues.Any(i => i.Severity == ValidationSeverity.Error))
            throw new InvalidDataException("The selected dataset changed or is invalid. Reload it before evaluation.");
        if (Directory.Exists(directory)) throw new IOException("The PSAPI snapshot directory already exists.");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);

        var queries = verified.Queries.Select(q => new JsonObject { ["id"] = q.Id, ["text"] = q.Text }).ToList();
        var corpus = new List<JsonObject>();
        var assets = DatasetManifest.Load(sourcePath).Assets;
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        foreach (var document in verified.Documents)
        {
            ct.ThrowIfCancellationRequested();
            if (document.ImagePath is not { } image)
            {
                corpus.Add(new JsonObject { ["id"] = document.Id, ["text"] = document.Text });
                continue;
            }
            Directory.CreateDirectory(Path.Combine(directory, "images"));
            var relative = Path.Combine("images", $"{corpus.Count:D8}{Path.GetExtension(image)}");
            var copy = Path.Combine(directory, relative);
            await using (var input = File.OpenRead(image))
            await using (var output = File.Create(copy))
                await input.CopyToAsync(output, ct);
            var hash = FileHash(copy);
            var expected = assets.FirstOrDefault(a => string.Equals(Path.GetFullPath(Path.Combine(sourceDirectory, a.Key)),
                Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase)).Value;
            if (expected is not null && !string.Equals(hash, "sha256:" + expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Dataset image changed while preparing PSAPI input. Reload the dataset.");
            corpus.Add(new JsonObject { ["id"] = document.Id, ["path"] = relative.Replace('\\', '/'), ["hash"] = hash });
        }
        // EvalKit requires a judgments reference to emit its metrics envelope. Do not invent safety labels:
        // rank results are mapped back to the original dataset; EvalKit summary metrics are not imported.
        var judgments = verified.Judgments.Select(j => new JsonObject
        {
            ["query_id"] = j.QueryId, ["doc_id"] = j.DocumentId,
            ["relevance"] = j.Harmful == true ? -1 : j.Grade
        }).ToList();
        var queryRef = await WriteTableAsync("queries.jsonl", queries, "text");
        var corpusRef = await WriteTableAsync("corpus.jsonl", corpus, verified.Documents.All(d => d.ImagePath is not null) ? "image"
            : verified.Documents.All(d => d.ImagePath is null) ? "text" : "mixed");
        var judgmentRef = await WriteTableAsync("judgements.jsonl", judgments, null);
        var identityText = $"payload_hash_v1\ncorpus[0]:{corpusRef["hash"]}\njudgements:{judgmentRef["hash"]}\nqueries:{queryRef["hash"]}\n";
        var hashValue = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityText))).ToLowerInvariant();
        var name = source.Name + " (local PSAPI input)";
        var manifest = new JsonObject
        {
            ["schema_version"] = 3,
            ["description"] = new JsonObject { ["name"] = name, ["version"] = source.Version,
                ["text"] = "Local execution adapter. Evaluate returned ranks against the original app dataset; do not treat these metrics as RAI approval evidence." },
            ["queries"] = queryRef, ["corpus"] = new JsonArray(corpusRef), ["judgements"] = judgmentRef,
            ["payload_hash"] = hashValue,
            ["metadata"] = new JsonObject { ["source_dataset"] = JsonSerializer.SerializeToNode(source.ToReference()),
                ["adapter"] = "smeval-psapi-v1", ["metrics_basis"] = "Original app dataset; unknown safety judgments remain unknown." }
        };
        var path = Path.Combine(directory, "dataset.manifest.json");
        await File.WriteAllTextAsync(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        return new(path, new(name, source.Version, hashValue), source.ToReference());

        async Task<JsonObject> WriteTableAsync(string file, List<JsonObject> rows, string? modality)
        {
            var full = Path.Combine(directory, file);
            await File.WriteAllLinesAsync(full, rows.Select(r => r.ToJsonString()), new UTF8Encoding(false), ct);
            return new JsonObject { ["path"] = file, ["count"] = rows.Count, ["hash"] = FileHash(full), ["modality"] = modality };
        }
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
